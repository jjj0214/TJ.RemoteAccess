// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 TJ.RemoteAccess contributors
#pragma once

#include "engine_session_gate.hpp"

#include <cstdint>
#include <string>

namespace tjra {

// Typed, in-process input request. This is not wire protocol data and is NOT
// proof of a remote peer's identity. The trusted local input agent constructs
// the payload only after the gateway binds a validated connection to a session.
enum class NativeInputKind : std::uint8_t { Keyboard, Pointer };

struct NativeInputEvent {
    NativeInputKind kind;
    std::uint32_t key_sym = 0;
    bool key_down = false;
    std::uint16_t x = 0;
    std::uint16_t y = 0;
    std::uint8_t button_mask = 0;

    static NativeInputEvent Keyboard(std::uint32_t key_sym, bool down) noexcept {
        NativeInputEvent event{};
        event.kind = NativeInputKind::Keyboard;
        event.key_sym = key_sym;
        event.key_down = down;
        return event;
    }

    static NativeInputEvent Pointer(std::uint16_t x, std::uint16_t y,
                                    std::uint8_t mask) noexcept {
        NativeInputEvent event{};
        event.kind = NativeInputKind::Pointer;
        event.x = x;
        event.y = y;
        event.button_mask = mask;
        return event;
    }
};

using TrustedNativeInputSink = bool (*)(void* context,
                                        const NativeInputEvent& event) noexcept;

// This class MUST live in the final trusted injection agent, not in a Viewer
// or an untrusted RFB message parser. All native keyboard/pointer input must
// pass this check; a second injection path is a security bypass.
//
// The caller supplies only server-side, authenticated session identity, a
// fresh gate-issued control epoch, and a server-owned monotonic sequence. The
// peer MUST NOT choose any of those values.
//
// The callback executes under the gate lock and must be short, noexcept,
// nonblocking, nonreentrant. Never call EngineSessionGate from the callback.
class NativeInputBridge final {
public:
    explicit NativeInputBridge(EngineSessionGate& gate) noexcept : gate_(gate) {}

    InputDispatchResult Dispatch(const std::string& session_id,
                                 const std::string& trusted_principal_id,
                                 std::uint64_t control_epoch,
                                 std::uint64_t input_sequence,
                                 const NativeInputEvent& event,
                                 TrustedNativeInputSink sink,
                                 void* sink_context) {
        if (!sink) return {Result::InvalidArgument, false};
        CallContext ctx{event, sink, sink_context};
        return gate_.DispatchInput(session_id, trusted_principal_id,
                                   control_epoch, input_sequence,
                                   &Inject, &ctx);
    }

private:
    struct CallContext {
        NativeInputEvent event;
        TrustedNativeInputSink sink;
        void* context;
    };

    static bool Inject(void* opaque) noexcept {
        auto* ctx = static_cast<CallContext*>(opaque);
        return ctx->sink(ctx->context, ctx->event);
    }

    EngineSessionGate& gate_;
};

} // namespace tjra
