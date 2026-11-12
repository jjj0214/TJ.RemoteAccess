// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 TJ.RemoteAccess contributors
#include "authenticated_session_coordinator.hpp"

#include <algorithm>
#include <limits>
#include <utility>

namespace tjra {

AuthenticatedSessionCoordinator::AuthenticatedSessionCoordinator(
    EngineSessionGate& gate, TrustedChannelAttestor attest,
    void* attestor_context) noexcept
    : gate_(gate), input_(gate), attest_(attest),
      attestor_context_(attestor_context) {}

AuthenticatedSessionCoordinator::~AuthenticatedSessionCoordinator() {
    std::lock_guard<std::mutex> lock(mu_);
    // The trusted input agent remains responsible for releasing actual
    // pressed keys/buttons; destroying policy state cannot do that.
    while (!connections_.empty())
        DisconnectLocked(connections_.begin()->first);
}

bool AuthenticatedSessionCoordinator::ValidEvidence(
    const AuthenticatedChannelEvidence& e) noexcept {
    if (e.principal_id.empty() || e.principal_id.size() > 256 ||
        e.channel_binding_id.size() < 16 || e.channel_binding_id.size() > 256 ||
        e.session_id.size() < 16 || e.session_id.size() > 128)
        return false;
    return std::all_of(e.session_id.begin(), e.session_id.end(),
                       [](unsigned char c) {
                           return (c >= 'a' && c <= 'z') ||
                                  (c >= 'A' && c <= 'Z') ||
                                  (c >= '0' && c <= '9') || c == '-' || c == '_';
                       });
}

bool AuthenticatedSessionCoordinator::AttestLocked(
    std::uint64_t connection_handle,
    AuthenticatedChannelEvidence* out) const noexcept {
    if (connection_handle == 0 || !attest_ || !out) return false;
    try {
        AuthenticatedChannelEvidence evidence;
        if (!attest_(attestor_context_, connection_handle, &evidence) ||
            !ValidEvidence(evidence)) return false;
        *out = std::move(evidence);
        return true;
    } catch (...) {
        return false; // Exception in transport attestation must fail closed.
    }
}

bool AuthenticatedSessionCoordinator::SameBindingLocked(
    std::uint64_t connection_handle, const Binding& bound) const noexcept {
    AuthenticatedChannelEvidence current;
    if (!AttestLocked(connection_handle, &current)) return false;
    return current.principal_id == bound.evidence.principal_id &&
           current.channel_binding_id == bound.evidence.channel_binding_id &&
           current.session_id == bound.evidence.session_id;
}

Result AuthenticatedSessionCoordinator::Reserve(
    std::uint64_t connection_handle, Clock::time_point deadline,
    Clock::time_point now) {
    std::lock_guard<std::mutex> lock(mu_);
    // Never permit a long-lived attach authorization.
    if (connection_handle == 0 || deadline <= now ||
        deadline - now > std::chrono::seconds(60)) return Result::InvalidArgument;
    (void)ExpireReservationsLocked(now);
    if (connections_.find(connection_handle) != connections_.end())
        return Result::DuplicateSession;
    AuthenticatedChannelEvidence evidence;
    if (!AttestLocked(connection_handle, &evidence)) return Result::Unauthorized;
    const std::string reserved_session_id = evidence.session_id;
    const std::string reserved_principal_id = evidence.principal_id;

    const Result result = gate_.Reserve(evidence.session_id, evidence.principal_id,
                                        deadline, now);
    if (result != Result::Ok) return result;
    try {
        Binding binding;
        binding.evidence = std::move(evidence);
        binding.deadline = deadline;
        connections_.emplace(connection_handle, std::move(binding));
    } catch (...) {
        // Never leave a reservation behind if allocation fails.
        gate_.End(reserved_session_id, reserved_principal_id);
        throw;
    }
    return Result::Ok;
}

Result AuthenticatedSessionCoordinator::Attach(
    std::uint64_t connection_handle, Clock::time_point now) {
    std::lock_guard<std::mutex> lock(mu_);
    const auto it = connections_.find(connection_handle);
    if (it == connections_.end()) return Result::NotFound;
    if (!SameBindingLocked(connection_handle, it->second))
        return Result::Unauthorized;
    if (it->second.attached) return Result::AlreadyAttached;
    const Result result = gate_.Attach(it->second.evidence.session_id,
                                       it->second.evidence.principal_id, now);
    if (result == Result::Ok) it->second.attached = true;
    return result;
}

Result AuthenticatedSessionCoordinator::GrantControl(
    std::uint64_t connection_handle) {
    std::lock_guard<std::mutex> lock(mu_);
    const auto it = connections_.find(connection_handle);
    if (it == connections_.end()) return Result::NotFound;
    if (!SameBindingLocked(connection_handle, it->second))
        return Result::Unauthorized;
    if (!it->second.attached) return Result::Unauthorized;
    return gate_.GrantControl(it->second.evidence.session_id,
                              it->second.evidence.principal_id, false);
}

InputDispatchResult AuthenticatedSessionCoordinator::DispatchInput(
    std::uint64_t connection_handle, const NativeInputEvent& event,
    TrustedNativeInputSink trusted_sink, void* sink_context) {
    std::lock_guard<std::mutex> lock(mu_);
    const auto it = connections_.find(connection_handle);
    if (it == connections_.end()) return {Result::NotFound, false};
    if (!SameBindingLocked(connection_handle, it->second))
        return {Result::Unauthorized, false};
    if (!it->second.attached) return {Result::Unauthorized, false};
    if (!trusted_sink) return {Result::InvalidArgument, false};
    if (event.kind != NativeInputKind::Keyboard &&
        event.kind != NativeInputKind::Pointer)
        return {Result::InvalidArgument, false};
    if (it->second.last_server_sequence ==
        (std::numeric_limits<std::uint64_t>::max)())
        return {Result::Replay, false};

    const auto next_sequence = ++it->second.last_server_sequence;
    const auto epoch = gate_.GetStats().control_epoch;
    const auto outcome = input_.Dispatch(
        it->second.evidence.session_id, it->second.evidence.principal_id,
        epoch, next_sequence, event, trusted_sink, sink_context);
    if (outcome.authorization == Result::Ok && !outcome.delivered) {
        // A partially failed native event can leave a key/button held.
        // Revoke the controller and require trusted physical-release ACK.
        (void)gate_.RevokeControl(it->second.evidence.principal_id, false);
    }
    return outcome;
}

EndResult AuthenticatedSessionCoordinator::DisconnectLocked(
    std::uint64_t connection_handle) {
    const auto it = connections_.find(connection_handle);
    if (it == connections_.end()) return {Result::NotFound, false};
    const auto session_id = it->second.evidence.session_id;
    const auto principal_id = it->second.evidence.principal_id;
    const EndResult result = gate_.End(session_id, principal_id);
    connections_.erase(it);
    return result;
}

EndResult AuthenticatedSessionCoordinator::Disconnect(
    std::uint64_t connection_handle) {
    std::lock_guard<std::mutex> lock(mu_);
    return DisconnectLocked(connection_handle);
}

std::size_t AuthenticatedSessionCoordinator::ExpireReservations(
    Clock::time_point now) {
    std::lock_guard<std::mutex> lock(mu_);
    return ExpireReservationsLocked(now);
}

std::size_t AuthenticatedSessionCoordinator::ExpireReservationsLocked(
    Clock::time_point now) {
    // Called under the coordinator mutex.
    std::size_t expired = 0;
    for (auto it = connections_.begin(); it != connections_.end();) {
        if (!it->second.attached && now >= it->second.deadline) {
            const auto handle = (it++)->first;
            (void)DisconnectLocked(handle);
            ++expired;
        } else {
            ++it;
        }
    }
    return expired;
}

AuthenticatedChannelSweepResult
AuthenticatedSessionCoordinator::SweepDisconnectedChannels() {
    std::lock_guard<std::mutex> lock(mu_);
    AuthenticatedChannelSweepResult report;
    for (auto it = connections_.begin(); it != connections_.end();) {
        const auto handle = it->first;
        if (SameBindingLocked(handle, it->second)) {
            ++it;
            continue;
        }
        ++it;
        const EndResult ended = DisconnectLocked(handle);
        ++report.disconnected;
        if (ended.input_release_required)
            report.input_release_required = true;
    }
    if (report.input_release_required)
        report.release_epoch = gate_.GetStats().control_epoch;
    return report;
}
std::size_t AuthenticatedSessionCoordinator::ActiveConnections() const {
    std::lock_guard<std::mutex> lock(mu_);
    return connections_.size();
}

} // namespace tjra
