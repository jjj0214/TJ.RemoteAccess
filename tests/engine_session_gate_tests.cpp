// SPDX-License-Identifier: GPL-2.0-or-later
#include "authenticated_session_coordinator.hpp"
#include "engine_session_gate.hpp"
#include "native_input_bridge.hpp"
#include "secure_session_id.hpp"

#include <atomic>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

using namespace tjra;

namespace {
int checks = 0;

void Check(bool condition, const char* message) {
    ++checks;
    if (!condition) throw std::runtime_error(message);
}

void Is(Result observed, Result expected) {
    ++checks;
    if (observed != expected)
        throw std::runtime_error(std::string("Expected ") + ResultName(expected) +
                                 ", got " + ResultName(observed));
}

bool CountInput(void* context) noexcept {
    auto* count = static_cast<int*>(context);
    ++*count;
    return true;
}

bool FailInput(void*) noexcept {
    return false;
}

void TestModesAndInput() {
    const auto now = Clock::now();
    const auto deadline = now + std::chrono::seconds(30);
    EngineSessionGate gate;
    Check(gate.GetMode() == AccessMode::Normal, "default Normal");
    Is(gate.Reserve("a", "p1", deadline, now), Result::Ok);
    Is(gate.Reserve("b", "p2", deadline, now), Result::CapacityReached);
    Is(gate.Attach("a", "p1", now), Result::Ok);
    Is(gate.GrantControl("a", "p1", false), Result::Ok);
    const auto first_epoch = gate.GetStats().control_epoch;
    int deliveries = 0;

    auto rejected = gate.DispatchInput("a", "intruder", first_epoch, 1,
                                       CountInput, &deliveries);
    Is(rejected.authorization, Result::WrongPrincipal);
    Check(!rejected.delivered && deliveries == 0, "wrong principal never injected");

    rejected = gate.DispatchInput("a", "p1", first_epoch, 1, nullptr, &deliveries);
    Is(rejected.authorization, Result::InvalidArgument);
    Check(deliveries == 0, "null input function rejected");

    auto dispatched = gate.DispatchInput("a", "p1", first_epoch, 1,
                                         CountInput, &deliveries);
    Is(dispatched.authorization, Result::Ok);
    Check(dispatched.delivered && deliveries == 1, "controller input");
    rejected = gate.DispatchInput("a", "p1", first_epoch, 1, CountInput, &deliveries);
    Is(rejected.authorization, Result::Replay);
    Check(deliveries == 1, "replay never injected");

    dispatched = gate.DispatchInput("a", "p1", first_epoch, 2, FailInput, nullptr);
    Is(dispatched.authorization, Result::Ok);
    Check(!dispatched.delivered, "input sink failure surfaced");
    rejected = gate.DispatchInput("a", "p1", first_epoch, 2, CountInput, &deliveries);
    Is(rejected.authorization, Result::Replay);

    Is(gate.SetMode(AccessMode::Maintenance), Result::Ok);
    Is(gate.Reserve("b", "p2", deadline, now), Result::Ok);
    Is(gate.Reserve("c", "p3", deadline, now), Result::Ok);
    Is(gate.Reserve("d", "p4", deadline, now), Result::CapacityReached);
    Is(gate.Attach("b", "p2", now), Result::Ok);
    Is(gate.Attach("c", "p3", now), Result::Ok);
    Is(gate.SetMode(AccessMode::Normal), Result::CapacityReached);
    Check(gate.GetMode() == AccessMode::Maintenance, "failed mode switch atomic");

    rejected = gate.DispatchInput("c", "p3", first_epoch, 1, CountInput, &deliveries);
    Is(rejected.authorization, Result::Unauthorized);
    Check(deliveries == 1, "viewer cannot inject");

    Is(gate.GrantControl("b", "p2", false), Result::ControlOccupied);
    Is(gate.GrantControl("b", "p1", false), Result::InputReleasePending);
    Check(gate.GetStats().controlling == 0, "no controller until release");
    rejected = gate.DispatchInput("a", "p1", first_epoch, 3, CountInput, &deliveries);
    Is(rejected.authorization, Result::InputReleasePending);
    Is(gate.ConfirmInputReleased(first_epoch), Result::StaleEpoch);
    const auto release_epoch = gate.GetStats().control_epoch;
    Is(gate.ConfirmInputReleased(release_epoch), Result::Ok);
    Check(gate.GetStats().controlling == 1, "controller after trusted ACK");
    const auto second_epoch = gate.GetStats().control_epoch;
    rejected = gate.DispatchInput("b", "p2", first_epoch, 1, CountInput, &deliveries);
    Is(rejected.authorization, Result::StaleEpoch);
    dispatched = gate.DispatchInput("b", "p2", second_epoch, 1,
                                    CountInput, &deliveries);
    Is(dispatched.authorization, Result::Ok);
    Check(dispatched.delivered && deliveries == 2, "new controller input");

    Is(gate.End("a", "p1").result, Result::Ok);
    Is(gate.End("c", "p3").result, Result::Ok);
    Is(gate.SetMode(AccessMode::Normal), Result::Ok);
    Is(gate.Reserve("d", "p4", deadline, now), Result::CapacityReached);
    Is(gate.RevokeControl("p2", false), Result::Ok);
    rejected = gate.DispatchInput("b", "p2", second_epoch, 2,
                                  CountInput, &deliveries);
    Is(rejected.authorization, Result::InputReleasePending);
    Is(gate.ConfirmInputReleased(gate.GetStats().control_epoch), Result::Ok);
    Is(gate.End("b", "p2").result, Result::Ok);
    Is(gate.Reserve("d", "p4", deadline, now), Result::Ok);
}

void TestPendingTransferCancellation() {
    const auto now = Clock::now();
    EngineSessionGate gate(AccessMode::Maintenance);
    for (int i = 0; i < 2; ++i) {
        const std::string session = "s" + std::to_string(i);
        const std::string principal = "p" + std::to_string(i);
        Is(gate.Reserve(session, principal, now + std::chrono::minutes(1), now),
           Result::Ok);
        Is(gate.Attach(session, principal, now), Result::Ok);
    }
    Is(gate.GrantControl("s0", "p0", false), Result::Ok);
    Is(gate.GrantControl("s1", "p0", false), Result::InputReleasePending);
    Is(gate.RevokeControl("p1", false), Result::Unauthorized);
    Is(gate.RevokeControl("admin", true), Result::Ok);
    Is(gate.ConfirmInputReleased(gate.GetStats().control_epoch), Result::Ok);
    Check(gate.GetStats().controlling == 0,
          "admin revoke must cancel pending control handoff");
    Is(gate.GrantControl("s1", "p1", false), Result::Ok);
}

void TestConcurrentReservations() {
    const auto now = Clock::now();
    for (auto mode : {AccessMode::Normal, AccessMode::Maintenance}) {
        EngineSessionGate gate(mode);
        std::vector<std::thread> threads;
        std::atomic<int> accepted{0};
        for (int i = 0; i < 64; ++i) {
            threads.emplace_back([&gate, &accepted, now, i] {
                if (gate.Reserve("session" + std::to_string(i),
                                 "principal" + std::to_string(i),
                                 now + std::chrono::seconds(30), now) == Result::Ok)
                    accepted.fetch_add(1);
            });
        }
        for (auto& thread : threads) thread.join();
        const int expected = mode == AccessMode::Normal ? 1 : 3;
        Check(accepted.load() == expected, "concurrent admission cap");
        Check(gate.GetStats().reserved == static_cast<std::size_t>(expected),
              "reservation count matches mode");
    }
}

void TestExpiredReservationReclaimed() {
    const auto now = Clock::now();
    EngineSessionGate gate;
    Is(gate.Reserve("old", "p", now + std::chrono::seconds(1), now), Result::Ok);
    Is(gate.Reserve("new", "p", now + std::chrono::seconds(5),
                    now + std::chrono::seconds(2)), Result::Ok);
    Check(gate.GetStats().reserved == 1, "reservation expiry on admission");
}
struct NativeRecorder {
    int calls = 0;
    NativeInputEvent last{};
};

bool RecordNativeInput(void* context, const NativeInputEvent& event) noexcept {
    auto* state = static_cast<NativeRecorder*>(context);
    ++state->calls;
    state->last = event;
    return true;
}

void TestTypedNativeInputBridge() {
    const auto now = Clock::now();
    EngineSessionGate gate;
    NativeInputBridge bridge(gate);
    NativeRecorder recorder;
    const auto key = NativeInputEvent::Keyboard(0x41U, true);
    auto denied = bridge.Dispatch("bound", "identity", 0, 1, key,
                                  &RecordNativeInput, &recorder);
    Is(denied.authorization, Result::NotFound);
    Check(!denied.delivered && recorder.calls == 0, "no unbound injection");

    Is(gate.Reserve("bound", "identity", now + std::chrono::seconds(60), now),
       Result::Ok);
    Is(gate.Attach("bound", "identity", now), Result::Ok);
    Is(gate.GrantControl("bound", "identity", false), Result::Ok);
    const auto epoch = gate.GetStats().control_epoch;

    denied = bridge.Dispatch("bound", "other", epoch, 1, key,
                             &RecordNativeInput, &recorder);
    Is(denied.authorization, Result::WrongPrincipal);
    Check(recorder.calls == 0, "wrong identity never reaches native sink");

    auto delivered = bridge.Dispatch("bound", "identity", epoch, 1, key,
                                     &RecordNativeInput, &recorder);
    Is(delivered.authorization, Result::Ok);
    Check(delivered.delivered && recorder.calls == 1, "keyboard delivered");
    Check(recorder.last.kind == NativeInputKind::Keyboard &&
          recorder.last.key_sym == 0x41U && recorder.last.key_down,
          "keyboard event preserved");

    denied = bridge.Dispatch("bound", "identity", epoch, 1, key,
                             &RecordNativeInput, &recorder);
    Is(denied.authorization, Result::Replay);
    Check(recorder.calls == 1, "typed keyboard replay blocked");

    const auto mouse = NativeInputEvent::Pointer(34, 56, 3);
    delivered = bridge.Dispatch("bound", "identity", epoch, 2, mouse,
                                &RecordNativeInput, &recorder);
    Is(delivered.authorization, Result::Ok);
    Check(delivered.delivered && recorder.calls == 2, "pointer delivered");
    Check(recorder.last.kind == NativeInputKind::Pointer &&
          recorder.last.x == 34 && recorder.last.y == 56 &&
          recorder.last.button_mask == 3, "pointer event preserved");

    denied = bridge.Dispatch("bound", "identity", epoch, 3, key,
                             nullptr, &recorder);
    Is(denied.authorization, Result::InvalidArgument);
    Is(gate.RevokeControl("identity", false), Result::Ok);
    denied = bridge.Dispatch("bound", "identity", epoch, 3, key,
                             &RecordNativeInput, &recorder);
    Is(denied.authorization, Result::InputReleasePending);
    Check(recorder.calls == 2, "revoked input never injected");
}

// This mock simulates a trusted mTLS transport solely to exercise policy.
// It does NOT implement certificate validation or provide a real trust boundary.
struct MockTransportPeer {
    AuthenticatedChannelEvidence evidence;
    bool authenticated = true;
    bool throw_on_attest = false;
};

struct MockAuthenticatedTransport {
    std::unordered_map<std::uint64_t, MockTransportPeer> peers;

    static bool Attest(void* opaque, std::uint64_t handle,
                       AuthenticatedChannelEvidence* evidence) {
        auto& transport = *static_cast<MockAuthenticatedTransport*>(opaque);
        const auto it = transport.peers.find(handle);
        if (it == transport.peers.end() || !it->second.authenticated)
            return false;
        if (it->second.throw_on_attest)
            throw std::runtime_error("simulated TLS failure");
        *evidence = it->second.evidence;
        return true;
    }
};

bool SimulateNativeInputFailure(void*, const NativeInputEvent&) noexcept {
    return false;
}

void TestAuthenticatedConnectionLifecycle() {
    const auto now = Clock::now();
    const auto deadline = now + std::chrono::minutes(1);
    EngineSessionGate gate;
    MockAuthenticatedTransport tls;
    tls.peers.emplace(1U, MockTransportPeer{{
        "authenticated-alice", "tls-exporter-connection-binding-A",
        "server-session-alice-000000001"}});
    AuthenticatedSessionCoordinator coordinator(gate,
        &MockAuthenticatedTransport::Attest, &tls);

    Is(coordinator.Reserve(0, deadline, now), Result::InvalidArgument);
    Is(coordinator.Reserve(7, deadline, now), Result::Unauthorized);
    Is(coordinator.Reserve(1, now, now), Result::InvalidArgument);
    Is(coordinator.Reserve(1, deadline, now), Result::Ok);
    Is(coordinator.Reserve(1, deadline, now), Result::DuplicateSession);
    Check(coordinator.ActiveConnections() == 1, "one bound channel");
    Is(coordinator.GrantControl(1), Result::Unauthorized);
    Is(coordinator.Attach(1, now), Result::Ok);
    Is(coordinator.Attach(1, now), Result::AlreadyAttached);
    Is(coordinator.GrantControl(1), Result::Ok);

    NativeRecorder recorder;
    const auto key = NativeInputEvent::Keyboard(0x41U, true);
    auto sent = coordinator.DispatchInput(1, key, &RecordNativeInput, &recorder);
    Is(sent.authorization, Result::Ok);
    Check(sent.delivered && recorder.calls == 1, "attested keyboard");
    sent = coordinator.DispatchInput(7, key, &RecordNativeInput, &recorder);
    Is(sent.authorization, Result::NotFound);
    Check(!sent.delivered, "unknown handle must not inject");

    tls.peers.at(1).evidence.channel_binding_id = "different-channel-binding-B";
    sent = coordinator.DispatchInput(1, key, &RecordNativeInput, &recorder);
    Is(sent.authorization, Result::Unauthorized);
    Check(!sent.delivered && recorder.calls == 1,
          "TLS channel substitution must not inject");
    tls.peers.at(1).evidence.channel_binding_id =
        "tls-exporter-connection-binding-A";
    tls.peers.at(1).authenticated = false;
    Is(coordinator.GrantControl(1), Result::Unauthorized);
    sent = coordinator.DispatchInput(1, key, &RecordNativeInput, &recorder);
    Is(sent.authorization, Result::Unauthorized);
    tls.peers.at(1).authenticated = true;
    tls.peers.at(1).throw_on_attest = true;
    sent = coordinator.DispatchInput(1, key, &RecordNativeInput, &recorder);
    Is(sent.authorization, Result::Unauthorized);
    tls.peers.at(1).throw_on_attest = false;

    sent = coordinator.DispatchInput(1, NativeInputEvent::Pointer(21, 33, 3),
                                     &RecordNativeInput, &recorder);
    Is(sent.authorization, Result::Ok);
    Check(sent.delivered && recorder.calls == 2, "attested pointer");
    auto invalid = NativeInputEvent::Keyboard(0U, false);
    invalid.kind = static_cast<NativeInputKind>(99);
    sent = coordinator.DispatchInput(1, invalid, &RecordNativeInput, &recorder);
    Is(sent.authorization, Result::InvalidArgument);
    sent = coordinator.DispatchInput(1, key, nullptr, &recorder);
    Is(sent.authorization, Result::InvalidArgument);
    sent = coordinator.DispatchInput(1, key, &SimulateNativeInputFailure, nullptr);
    Is(sent.authorization, Result::Ok);
    Check(!sent.delivered && gate.GetStats().input_release_pending,
          "failed injection revokes controller until physical key release");
    sent = coordinator.DispatchInput(1, key, &RecordNativeInput, &recorder);
    Is(sent.authorization, Result::InputReleasePending);
    Is(coordinator.GrantControl(1), Result::InputReleasePending);
    Is(gate.ConfirmInputReleased(gate.GetStats().control_epoch), Result::Ok);
    Is(coordinator.GrantControl(1), Result::Ok);
    auto ended = coordinator.Disconnect(1);
    Is(ended.result, Result::Ok);
    Check(ended.input_release_required, "controller disconnect requests key release");
    Is(coordinator.Disconnect(1).result, Result::NotFound);
    Check(coordinator.ActiveConnections() == 0, "closed channel erased");
    Check(gate.GetStats().viewing == 0, "closed reservation removed");
}

void TestAuthenticatedAdmissionAndExpiry() {
    const auto now = Clock::now();
    const auto deadline = now + std::chrono::seconds(30);
    EngineSessionGate gate(AccessMode::Maintenance);
    MockAuthenticatedTransport tls;
    tls.peers.emplace(11U, MockTransportPeer{{
        "alice", "tls-binding-alice-000001", "generated-session-alice-001"}});
    tls.peers.emplace(12U, MockTransportPeer{{
        "bob", "tls-binding-bob-0000002", "generated-session-bob-00002"}});
    tls.peers.emplace(13U, MockTransportPeer{{
        "other", "tls-binding-other-00003", "generated-session-alice-001"}});
    tls.peers.emplace(14U, MockTransportPeer{{
        "charlie", "tls-binding-charlie-004", "generated-session-charlie-04"}});
    tls.peers.emplace(15U, MockTransportPeer{{
        "dave", "tls-binding-dave-000005", "generated-session-dave-005"}});
    AuthenticatedSessionCoordinator coordinator(gate,
        &MockAuthenticatedTransport::Attest, &tls);

    Is(coordinator.Reserve(11, now + std::chrono::seconds(61), now),
       Result::InvalidArgument);
    tls.peers.at(11).evidence.channel_binding_id = "short";
    Is(coordinator.Reserve(11, deadline, now), Result::Unauthorized);
    tls.peers.at(11).evidence.channel_binding_id = "tls-binding-alice-000001";
    Is(coordinator.Reserve(11, deadline, now), Result::Ok);
    Is(coordinator.Reserve(13, deadline, now), Result::DuplicateSession);
    tls.peers.at(13).evidence.session_id = "generated-session-other-003";
    Is(coordinator.Attach(11, now), Result::Ok);
    Is(coordinator.Reserve(12, deadline, now), Result::Ok);
    Is(coordinator.Attach(12, now), Result::Ok);
    Is(coordinator.Reserve(14, now + std::chrono::seconds(1), now), Result::Ok);
    Is(coordinator.Reserve(15, deadline, now), Result::CapacityReached);
    Check(coordinator.ExpireReservations(now + std::chrono::seconds(2)) == 1,
          "expired reservation removed");
    Is(coordinator.Attach(14, now + std::chrono::seconds(2)), Result::NotFound);
    Is(coordinator.Reserve(15, deadline, now + std::chrono::seconds(2)),
       Result::Ok);
    Is(coordinator.Attach(15, now + std::chrono::seconds(2)), Result::Ok);
    Is(gate.SetMode(AccessMode::Normal), Result::CapacityReached);
    Is(coordinator.GrantControl(12), Result::Ok);
    Is(coordinator.GrantControl(11), Result::ControlOccupied);
    const auto ended = coordinator.Disconnect(12);
    Is(ended.result, Result::Ok);
    Check(ended.input_release_required, "controller close requests physical release");
    Is(coordinator.GrantControl(11), Result::InputReleasePending);
    Is(gate.ConfirmInputReleased(gate.GetStats().control_epoch), Result::Ok);
    Is(coordinator.GrantControl(11), Result::Ok);
    Is(coordinator.Disconnect(11).result, Result::Ok);
    Is(coordinator.Disconnect(15).result, Result::Ok);
    Check(coordinator.ActiveConnections() == 0, "all connections cleaned");
}

void TestAutomaticExpiryBeforeNewAdmission() {
    const auto now = Clock::now();
    EngineSessionGate gate;
    MockAuthenticatedTransport transport;
    transport.peers.emplace(101U, MockTransportPeer{{
        "alice", "tls-expiry-binding-alice-001", "server-expiry-alice-0000001"}});
    transport.peers.emplace(102U, MockTransportPeer{{
        "bob", "tls-expiry-binding-bob-00002", "server-expiry-bob-00000002"}});
    AuthenticatedSessionCoordinator coordinator(gate,
        &MockAuthenticatedTransport::Attest, &transport);
    Is(coordinator.Reserve(101, now + std::chrono::seconds(1), now),
       Result::Ok);
    Is(coordinator.Reserve(102, now + std::chrono::seconds(10),
                           now + std::chrono::seconds(2)), Result::Ok);
    Check(coordinator.ActiveConnections() == 1,
          "new admission must purge expired transport binding");
    Is(coordinator.Attach(101, now + std::chrono::seconds(2)), Result::NotFound);
    Is(coordinator.Attach(102, now + std::chrono::seconds(2)), Result::Ok);
    Is(coordinator.Disconnect(102).result, Result::Ok);
}
void TestSecureSessionIds() {
    std::string first = "stale";
    std::string second;
    Check(!GenerateSecureSessionId(nullptr), "null output pointer rejected");
#ifdef _WIN32
    Check(GenerateSecureSessionId(&first), "Windows CNG identifier generated");
    Check(GenerateSecureSessionId(&second), "independent CNG identifier generated");
    Check(first.size() == 64 && second.size() == 64, "256-bit hex identifiers");
    for (const char c : first)
        Check((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
              "lowercase hexadecimal identifier");
    Check(first != second, "distinct CNG identifiers");
#else
    Check(!GenerateSecureSessionId(&first), "no insecure platform fallback");
    Check(first.empty(), "failed generation clears output");
#endif
}
void TestDisconnectedChannelSweep() {
    const auto now = Clock::now();
    const auto deadline = now + std::chrono::seconds(20);
    EngineSessionGate gate(AccessMode::Maintenance);
    MockAuthenticatedTransport tls;
    tls.peers.emplace(81U, MockTransportPeer{{
        "alice", "tls-sweep-binding-alice-0001", "server-sweep-alice-000001"}});
    tls.peers.emplace(82U, MockTransportPeer{{
        "bob", "tls-sweep-binding-bob-00002", "server-sweep-bob-00000002"}});
    AuthenticatedSessionCoordinator coordinator(gate,
        &MockAuthenticatedTransport::Attest, &tls);
    Is(coordinator.Reserve(81, deadline, now), Result::Ok);
    Is(coordinator.Attach(81, now), Result::Ok);
    Is(coordinator.Reserve(82, deadline, now), Result::Ok);
    Is(coordinator.Attach(82, now), Result::Ok);
    Is(coordinator.GrantControl(81), Result::Ok);
    tls.peers.at(81).authenticated = false;
    const auto result = coordinator.SweepDisconnectedChannels();
    Check(result.disconnected == 1 && result.input_release_required,
          "disconnected controller reports need for native release");
    Check(result.release_epoch == gate.GetStats().control_epoch,
          "sweep includes exact key-release epoch");
    Check(coordinator.ActiveConnections() == 1, "sweep retains live viewer");
    Check(gate.GetStats().input_release_pending,
          "sweep cannot acknowledge key release itself");
    NativeRecorder recorder;
    auto denied = coordinator.DispatchInput(
        81, NativeInputEvent::Keyboard(0x41U, false),
        &RecordNativeInput, &recorder);
    Is(denied.authorization, Result::NotFound);
    Is(coordinator.GrantControl(82), Result::InputReleasePending);
    Is(gate.ConfirmInputReleased(result.release_epoch), Result::Ok);
    Is(coordinator.GrantControl(82), Result::Ok);
    Check(coordinator.SweepDisconnectedChannels().disconnected == 0,
          "healthy authenticated channel survives sweep");
    tls.peers.at(82).evidence.channel_binding_id = "new-transport-binding-bob";
    const auto substituted = coordinator.SweepDisconnectedChannels();
    Check(substituted.disconnected == 1 && substituted.input_release_required,
          "channel substitution revokes control and requires physical release");
    Check(coordinator.ActiveConnections() == 0, "all invalid channels removed");
}
} // namespace

int main() {
    try {
        TestModesAndInput();
        TestPendingTransferCancellation();
        TestConcurrentReservations();
        TestExpiredReservationReclaimed();
        TestTypedNativeInputBridge();
        TestAuthenticatedConnectionLifecycle();
        TestAuthenticatedAdmissionAndExpiry();
        TestAutomaticExpiryBeforeNewAdmission();
        TestSecureSessionIds();
        TestDisconnectedChannelSweep();
        std::cout << "TJ.RemoteAccess M2 EngineSessionGate PASS: "
                  << checks << " checks" << std::endl;
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "TJ.RemoteAccess M2 EngineSessionGate FAIL after "
                  << checks << " checks: " << ex.what() << std::endl;
        return 1;
    }
}
