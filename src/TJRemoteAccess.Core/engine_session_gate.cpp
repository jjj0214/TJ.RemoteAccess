// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 TJ.RemoteAccess contributors
#include "engine_session_gate.hpp"

namespace tjra {

EngineSessionGate::EngineSessionGate(AccessMode initial_mode)
    : mode_(initial_mode) {}

std::size_t EngineSessionGate::CapacityLocked() const {
    return mode_ == AccessMode::Maintenance ? 3U : 1U;
}

Result EngineSessionGate::SetMode(AccessMode mode) {
    std::lock_guard<std::mutex> guard(mutex_);
    const auto stats = registry_.GetStats();
    const std::size_t used = stats.reserved + stats.viewing;
    const std::size_t next_cap = mode == AccessMode::Maintenance ? 3U : 1U;
    if (used > next_cap) return Result::CapacityReached;
    mode_ = mode;
    return Result::Ok;
}

AccessMode EngineSessionGate::GetMode() const {
    std::lock_guard<std::mutex> guard(mutex_);
    return mode_;
}

Result EngineSessionGate::Reserve(const std::string& session_id,
                                  const std::string& trusted_principal_id,
                                  Clock::time_point deadline,
                                  Clock::time_point now) {
    std::lock_guard<std::mutex> guard(mutex_);
    // Expired but uncollected reservations cannot block legitimate clients.
    registry_.ExpireReservations(now);
    const auto stats = registry_.GetStats();
    if (stats.reserved + stats.viewing >= CapacityLocked())
        return Result::CapacityReached;
    return registry_.Reserve(session_id, trusted_principal_id, deadline, now);
}

Result EngineSessionGate::Attach(const std::string& session_id,
                                 const std::string& trusted_principal_id,
                                 Clock::time_point now) {
    std::lock_guard<std::mutex> guard(mutex_);
    return registry_.Attach(session_id, trusted_principal_id, now);
}

Result EngineSessionGate::GrantControl(const std::string& session_id,
                                       const std::string& trusted_actor_principal_id,
                                       bool actor_is_admin) {
    std::lock_guard<std::mutex> guard(mutex_);
    return registry_.GrantControl(session_id, trusted_actor_principal_id,
                                  actor_is_admin);
}

Result EngineSessionGate::RevokeControl(const std::string& trusted_actor_principal_id,
                                        bool actor_is_admin) {
    std::lock_guard<std::mutex> guard(mutex_);
    return registry_.RevokeControl(trusted_actor_principal_id, actor_is_admin);
}

EndResult EngineSessionGate::End(const std::string& session_id,
                                 const std::string& trusted_actor_principal_id,
                                 bool actor_is_admin) {
    std::lock_guard<std::mutex> guard(mutex_);
    return registry_.End(session_id, trusted_actor_principal_id, actor_is_admin);
}

Result EngineSessionGate::ConfirmInputReleased(std::uint64_t release_epoch) {
    std::lock_guard<std::mutex> guard(mutex_);
    return registry_.ConfirmInputReleased(release_epoch);
}

std::size_t EngineSessionGate::ExpireReservations(Clock::time_point now) {
    std::lock_guard<std::mutex> guard(mutex_);
    return registry_.ExpireReservations(now);
}

Stats EngineSessionGate::GetStats() const {
    std::lock_guard<std::mutex> guard(mutex_);
    return registry_.GetStats();
}

InputDispatchResult EngineSessionGate::DispatchInput(
    const std::string& session_id,
    const std::string& trusted_principal_id,
    std::uint64_t control_epoch,
    std::uint64_t input_sequence,
    bool (*trusted_inject)(void*) noexcept,
    void* context) {
    if (trusted_inject == nullptr)
        return {Result::InvalidArgument, false};
    std::lock_guard<std::mutex> guard(mutex_);
    const Result result = registry_.AuthorizeInput(
        session_id, trusted_principal_id, control_epoch, input_sequence);
    if (result != Result::Ok) return {result, false};
    // Trusted, non-reentrant, non-blocking input operation only.
    return {Result::Ok, trusted_inject(context)};
}

} // namespace tjra
