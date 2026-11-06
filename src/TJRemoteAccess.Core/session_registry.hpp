// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 TJ.RemoteAccess contributors
#pragma once

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

namespace tjra {

// Credentials are established at the authenticated gateway, not extracted from
// unauthenticated RFB traffic or client-supplied machine/user names.
using Clock = std::chrono::steady_clock;

enum class SessionState { Reserved, Viewing };
enum class Result {
    Ok, InvalidArgument, CapacityReached, DuplicateSession,
    NotFound, WrongPrincipal, Expired, AlreadyAttached,
    Unauthorized, StaleEpoch, Replay, ControlOccupied, InputReleasePending
};

struct SessionSnapshot {
    std::string session_id;
    std::string principal_id;
    SessionState state;
    bool is_controller;
    std::uint64_t control_epoch;
};

struct Stats {
    std::size_t reserved;
    std::size_t viewing;
    std::size_t controlling;
    std::uint64_t control_epoch;
    bool input_release_pending;
};

struct EndResult {
    Result result;
    bool input_release_required;
};

// Policy state, not a network security boundary. A caller must authenticate
// the principal, securely generate session IDs and enforce input at the final
// privileged injection boundary. Physical key releases belong to that agent.
class SessionRegistry final {
public:
    explicit SessionRegistry(std::size_t max_sessions = 3);

    Result Reserve(const std::string& session_id,
                   const std::string& trusted_principal_id,
                   Clock::time_point deadline,
                   Clock::time_point now);
    Result Attach(const std::string& session_id,
                  const std::string& trusted_principal_id,
                  Clock::time_point now);
    Result GrantControl(const std::string& target_session_id,
                        const std::string& trusted_actor_principal_id,
                        bool actor_is_admin);
    Result RevokeControl(const std::string& trusted_actor_principal_id,
                         bool actor_is_admin);
    Result AuthorizeInput(const std::string& session_id,
                          const std::string& trusted_principal_id,
                          std::uint64_t expected_control_epoch,
                          std::uint64_t sequence);
    EndResult End(const std::string& session_id,
                  const std::string& trusted_actor_principal_id,
                  bool actor_is_admin = false);
    // Trusted input agent must confirm the physical release of any held keys /
    // buttons before a newly transferred controller may send input.
    Result ConfirmInputReleased(std::uint64_t release_epoch);
    // Expire un-attached reservations. Authenticated live sessions need a
    // separately enforced heartbeat/lease policy at the gateway.
    std::size_t ExpireReservations(Clock::time_point now);
    Stats GetStats() const;

private:
    struct Session {
        std::string principal_id;
        Clock::time_point deadline;
        SessionState state = SessionState::Reserved;
        bool is_controller = false;
        std::uint64_t last_input_sequence = 0;
    };
    mutable std::mutex mu_;
    std::unordered_map<std::string, Session> sessions_;
    const std::size_t max_sessions_;
    std::string controller_session_id_;
    std::uint64_t control_epoch_ = 0;
    bool input_release_pending_ = false;
    std::string pending_target_session_id_;

    void ClearControlLocked();
};

const char* ResultName(Result result);

} // namespace tjra
