// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 TJ.RemoteAccess contributors
#pragma once

#include "session_registry.hpp"

#include <cstdint>
#include <mutex>
#include <string>

namespace tjra {

// This adapter is a policy/injection boundary, NOT an RFB listener or a
// substitute for authenticating the principal in a trusted Windows/Gateway
// channel. Do not feed it identity strings supplied by an RFB peer.
enum class AccessMode { Normal, Maintenance };

struct InputDispatchResult {
    Result authorization;
    bool delivered;
};

class EngineSessionGate final {
public:
    explicit EngineSessionGate(AccessMode initial_mode = AccessMode::Normal);

    // Refuse a transition to Normal while more than one reservation/connection
    // exists. Never evict an active session merely to change the mode.
    Result SetMode(AccessMode mode);
    AccessMode GetMode() const;

    Result Reserve(const std::string& session_id,
                   const std::string& trusted_principal_id,
                   Clock::time_point deadline,
                   Clock::time_point now);
    Result Attach(const std::string& session_id,
                  const std::string& trusted_principal_id,
                  Clock::time_point now);
    Result GrantControl(const std::string& session_id,
                        const std::string& trusted_actor_principal_id,
                        bool actor_is_admin);
    Result RevokeControl(const std::string& trusted_actor_principal_id,
                         bool actor_is_admin);
    EndResult End(const std::string& session_id,
                  const std::string& trusted_actor_principal_id,
                  bool actor_is_admin = false);
    Result ConfirmInputReleased(std::uint64_t release_epoch);
    std::size_t ExpireReservations(Clock::time_point now);
    Stats GetStats() const;

    // Only the final trusted input agent may call this. Keep authentication
    // outside the RFB parser and perform the authorization AND physical input
    // injection within the same lock, so revoke/disconnect cannot race between
    // the check and the input. The callback must be short, noexcept, and must
    // NOT call back into EngineSessionGate.
    //
    // If delivered is false after successful authorization, the sequence was
    // nevertheless consumed. Never replay an input after an injection failure.
    InputDispatchResult DispatchInput(const std::string& session_id,
                                      const std::string& trusted_principal_id,
                                      std::uint64_t control_epoch,
                                      std::uint64_t input_sequence,
                                      bool (*trusted_inject)(void*) noexcept,
                                      void* context);

private:
    std::size_t CapacityLocked() const;

    mutable std::mutex mutex_;
    SessionRegistry registry_{3};
    AccessMode mode_;
};

} // namespace tjra
