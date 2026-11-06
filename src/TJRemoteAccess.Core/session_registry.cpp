// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 TJ.RemoteAccess contributors
#include "session_registry.hpp"

#include <stdexcept>
#include <utility>

namespace tjra {

SessionRegistry::SessionRegistry(std::size_t max_sessions)
    : max_sessions_(max_sessions) {
    if (max_sessions < 1 || max_sessions > 3)
        throw std::invalid_argument("max_sessions must be 1..3");
}

Result SessionRegistry::Reserve(const std::string& session_id,
                                const std::string& principal_id,
                                Clock::time_point deadline,
                                Clock::time_point now) {
    if (session_id.empty() || principal_id.empty() || deadline <= now)
        return Result::InvalidArgument;
    std::lock_guard<std::mutex> lock(mu_);
    if (sessions_.find(session_id) != sessions_.end())
        return Result::DuplicateSession;
    if (sessions_.size() >= max_sessions_)
        return Result::CapacityReached;
    sessions_.emplace(session_id, Session{principal_id, deadline});
    return Result::Ok;
}

Result SessionRegistry::Attach(const std::string& session_id,
                               const std::string& principal_id,
                               Clock::time_point now) {
    std::lock_guard<std::mutex> lock(mu_);
    auto it = sessions_.find(session_id);
    if (it == sessions_.end()) return Result::NotFound;
    Session& session = it->second;
    if (session.principal_id != principal_id) return Result::WrongPrincipal;
    if (session.state != SessionState::Reserved) return Result::AlreadyAttached;
    if (now >= session.deadline) return Result::Expired;
    session.state = SessionState::Viewing;
    return Result::Ok;
}

void SessionRegistry::ClearControlLocked() {
    if (controller_session_id_.empty()) return;
    auto it = sessions_.find(controller_session_id_);
    if (it != sessions_.end()) it->second.is_controller = false;
    controller_session_id_.clear();
    input_release_pending_ = true;
    ++control_epoch_; // Invalidate all in-flight input carrying old epoch.
}

Result SessionRegistry::GrantControl(const std::string& target_session_id,
                                     const std::string& actor_principal,
                                     bool actor_is_admin) {
    if (actor_principal.empty()) return Result::Unauthorized;
    std::lock_guard<std::mutex> lock(mu_);
    auto it = sessions_.find(target_session_id);
    if (it == sessions_.end()) return Result::NotFound;
    if (it->second.state != SessionState::Viewing) return Result::Unauthorized;
    if (input_release_pending_) return Result::InputReleasePending;
    if (controller_session_id_ == target_session_id)
        return it->second.principal_id == actor_principal || actor_is_admin
                   ? Result::Ok : Result::Unauthorized;
    // First controller can claim its own control. Existing controller may
    // transfer to any active viewer; admins can override the controller.
    bool allowed = actor_is_admin;
    if (controller_session_id_.empty())
        allowed = allowed || it->second.principal_id == actor_principal;
    else {
        const auto holder = sessions_.find(controller_session_id_);
        allowed = allowed || (holder != sessions_.end() &&
                             holder->second.principal_id == actor_principal);
    }
    if (!allowed) return Result::ControlOccupied;
    if (!controller_session_id_.empty()) {
        ClearControlLocked();
        pending_target_session_id_ = target_session_id;
        return Result::InputReleasePending;
    }
    it->second.is_controller = true;
    it->second.last_input_sequence = 0;
    controller_session_id_ = target_session_id;
    ++control_epoch_; // Fresh epoch, including initial grant.
    return Result::Ok;
}

Result SessionRegistry::RevokeControl(const std::string& actor_principal,
                                      bool actor_is_admin) {
    if (actor_principal.empty()) return Result::Unauthorized;
    std::lock_guard<std::mutex> lock(mu_);
    if (controller_session_id_.empty()) return Result::Ok;
    const auto it = sessions_.find(controller_session_id_);
    if (!actor_is_admin && (it == sessions_.end() ||
                            it->second.principal_id != actor_principal))
        return Result::Unauthorized;
    ClearControlLocked();
    return Result::Ok;
}

Result SessionRegistry::AuthorizeInput(const std::string& session_id,
                                       const std::string& principal_id,
                                       std::uint64_t epoch,
                                       std::uint64_t sequence) {
    std::lock_guard<std::mutex> lock(mu_);
    const auto it = sessions_.find(session_id);
    if (it == sessions_.end()) return Result::NotFound;
    Session& session = it->second;
    if (session.principal_id != principal_id) return Result::WrongPrincipal;
    if (input_release_pending_) return Result::InputReleasePending;
    if (!session.is_controller || session.state != SessionState::Viewing)
        return Result::Unauthorized;
    if (epoch != control_epoch_) return Result::StaleEpoch;
    if (sequence == 0 || sequence <= session.last_input_sequence)
        return Result::Replay;
    session.last_input_sequence = sequence;
    return Result::Ok;
}

EndResult SessionRegistry::End(const std::string& session_id,
                               const std::string& actor_principal,
                               bool actor_is_admin) {
    std::lock_guard<std::mutex> lock(mu_);
    const auto it = sessions_.find(session_id);
    if (it == sessions_.end()) return {Result::NotFound, false};
    if (actor_principal.empty() || (!actor_is_admin && it->second.principal_id != actor_principal))
        return {Result::Unauthorized, false};
    const bool release = it->second.is_controller;
    if (pending_target_session_id_ == session_id) pending_target_session_id_.clear();
    if (release) ClearControlLocked();
    sessions_.erase(it);
    return {Result::Ok, release};
}

Result SessionRegistry::ConfirmInputReleased(std::uint64_t release_epoch) {
    std::lock_guard<std::mutex> lock(mu_);
    if (!input_release_pending_ || release_epoch != control_epoch_)
        return Result::StaleEpoch;
    input_release_pending_ = false;
    if (!pending_target_session_id_.empty()) {
        const auto it = sessions_.find(pending_target_session_id_);
        if (it != sessions_.end() && it->second.state == SessionState::Viewing) {
            it->second.is_controller = true;
            it->second.last_input_sequence = 0;
            controller_session_id_ = pending_target_session_id_;
            ++control_epoch_;
        }
        pending_target_session_id_.clear();
    }
    return Result::Ok;
}

std::size_t SessionRegistry::ExpireReservations(Clock::time_point now) {
    std::lock_guard<std::mutex> lock(mu_);
    std::size_t count = 0;
    for (auto it = sessions_.begin(); it != sessions_.end();) {
        if (it->second.state == SessionState::Reserved &&
            now >= it->second.deadline) {
            it = sessions_.erase(it);
            ++count;
        } else {
            ++it;
        }
    }
    return count;
}

Stats SessionRegistry::GetStats() const {
    std::lock_guard<std::mutex> lock(mu_);
    Stats stats{};
    stats.control_epoch = control_epoch_;
    stats.input_release_pending = input_release_pending_;
    for (const auto& entry : sessions_) {
        if (entry.second.state == SessionState::Reserved) ++stats.reserved;
        else ++stats.viewing;
        if (entry.second.is_controller) ++stats.controlling;
    }
    return stats;
}

const char* ResultName(Result result) {
    switch (result) {
    case Result::Ok: return "Ok";
    case Result::InvalidArgument: return "InvalidArgument";
    case Result::CapacityReached: return "CapacityReached";
    case Result::DuplicateSession: return "DuplicateSession";
    case Result::NotFound: return "NotFound";
    case Result::WrongPrincipal: return "WrongPrincipal";
    case Result::Expired: return "Expired";
    case Result::AlreadyAttached: return "AlreadyAttached";
    case Result::Unauthorized: return "Unauthorized";
    case Result::StaleEpoch: return "StaleEpoch";
    case Result::Replay: return "Replay";
    case Result::ControlOccupied: return "ControlOccupied";
    case Result::InputReleasePending: return "InputReleasePending";
    }
    return "Unknown";
}

} // namespace tjra
