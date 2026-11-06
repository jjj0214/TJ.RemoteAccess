// SPDX-License-Identifier: GPL-2.0-or-later
#include "session_registry.hpp"
#include <atomic>
#include <chrono>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using namespace tjra;
namespace {
int checks = 0;
void Require(bool condition, const char* message) {
    ++checks;
    if (!condition) throw std::runtime_error(message);
}
void Is(Result actual, Result expected) {
    if (actual != expected)
        throw std::runtime_error(std::string("expected ") + ResultName(expected) +
                                 ", got " + ResultName(actual));
    ++checks;
}
} // namespace

int main() {
    try {
        const auto t0 = Clock::now();
        const auto deadline = t0 + std::chrono::seconds(30);
        SessionRegistry sessions;
        Is(sessions.Reserve("", "m1", deadline, t0), Result::InvalidArgument);
        Is(sessions.Reserve("a", "", deadline, t0), Result::InvalidArgument);
        Is(sessions.Reserve("a", "m1", t0, t0), Result::InvalidArgument);
        Is(sessions.Reserve("a", "m1", deadline, t0), Result::Ok);
        Is(sessions.Reserve("a", "m1", deadline, t0), Result::DuplicateSession);
        Is(sessions.Reserve("b", "m2", deadline, t0), Result::Ok);
        Is(sessions.Reserve("c", "m3", deadline, t0), Result::Ok);
        Is(sessions.Reserve("d", "m4", deadline, t0), Result::CapacityReached);
        Require(sessions.GetStats().reserved == 3, "reserved count");
        Is(sessions.Attach("a", "m2", t0), Result::WrongPrincipal);
        Is(sessions.Attach("a", "m1", t0), Result::Ok);
        Is(sessions.Attach("a", "m1", t0), Result::AlreadyAttached);
        Is(sessions.GrantControl("b", "m2", false), Result::Unauthorized);
        Is(sessions.GrantControl("a", "m1", false), Result::Ok);
        auto epoch = sessions.GetStats().control_epoch;
        Require(epoch > 0, "control epoch positive");
        Is(sessions.AuthorizeInput("b", "m2", epoch, 1), Result::Unauthorized);
        Is(sessions.AuthorizeInput("a", "wrong", epoch, 1), Result::WrongPrincipal);
        Is(sessions.AuthorizeInput("a", "m1", epoch - 1, 1), Result::StaleEpoch);
        Is(sessions.AuthorizeInput("a", "m1", epoch, 0), Result::Replay);
        Is(sessions.AuthorizeInput("a", "m1", epoch, 1), Result::Ok);
        Is(sessions.AuthorizeInput("a", "m1", epoch, 1), Result::Replay);
        Is(sessions.Attach("b", "m2", t0), Result::Ok);
        Is(sessions.GrantControl("b", "m2", false), Result::ControlOccupied);
        Is(sessions.GrantControl("b", "m1", false), Result::InputReleasePending);
        Require(sessions.GetStats().controlling == 0, "input must be released before transfer");
        Require(sessions.GetStats().input_release_pending, "physical release must be acknowledged");
        Is(sessions.AuthorizeInput("b", "m2", epoch, 1), Result::InputReleasePending);
        Is(sessions.ConfirmInputReleased(epoch), Result::StaleEpoch);
        Is(sessions.ConfirmInputReleased(sessions.GetStats().control_epoch), Result::Ok);
        Require(sessions.GetStats().controlling == 1, "single controller");
        Is(sessions.AuthorizeInput("a", "m1", epoch, 2), Result::Unauthorized);
        auto epoch2 = sessions.GetStats().control_epoch;
        Require(epoch2 > epoch, "epoch changed after transfer");
        Is(sessions.AuthorizeInput("b", "m2", epoch, 1), Result::StaleEpoch);
        Is(sessions.AuthorizeInput("b", "m2", epoch2, 1), Result::Ok);
        Is(sessions.RevokeControl("m1", false), Result::Unauthorized);
        Is(sessions.RevokeControl("m2", false), Result::Ok);
        Is(sessions.AuthorizeInput("b", "m2", epoch2, 2), Result::InputReleasePending);
        Is(sessions.GrantControl("b", "admin", true), Result::InputReleasePending);
        Is(sessions.ConfirmInputReleased(sessions.GetStats().control_epoch), Result::Ok);
        Is(sessions.GrantControl("b", "admin", true), Result::Ok);
        Is(sessions.End("b", "m3").result, Result::Unauthorized);
        Require(sessions.End("b", "m2").input_release_required, "release physical input on controller close");
        Is(sessions.GrantControl("a", "admin", true), Result::InputReleasePending);
        Is(sessions.ConfirmInputReleased(sessions.GetStats().control_epoch), Result::Ok);
        Require(sessions.GetStats().controlling == 0, "controller released");
        Require(!sessions.End("a", "m1").input_release_required, "viewer does not release");
        Is(sessions.End("unknown", "m1").result, Result::NotFound);
        Require(sessions.ExpireReservations(deadline) == 1, "expire pending c");
        Require(sessions.GetStats().reserved == 0, "expired removed");
        Is(sessions.Reserve("expired", "m1", deadline, t0), Result::Ok);
        Is(sessions.Attach("expired", "m1", deadline), Result::Expired);
        Require(sessions.ExpireReservations(deadline) == 1, "remove expired");

        // Concurrent reserve operations must never exceed the configured cap.
        SessionRegistry parallel;
        std::atomic<int> accepted{0};
        std::vector<std::thread> threads;
        for (int i = 0; i < 64; ++i) {
            threads.emplace_back([&parallel, &accepted, t0, deadline, i]() {
                const auto result = parallel.Reserve("id" + std::to_string(i),
                                                      "machine" + std::to_string(i),
                                                      deadline, t0);
                if (result == Result::Ok) accepted.fetch_add(1);
            });
        }
        for (auto& thread : threads) thread.join();
        Require(accepted.load() == 3, "atomic cap across threads");
        Require(parallel.GetStats().reserved == 3, "3 reservation cap");
        std::cout << "TJ.RemoteAccess Core tests PASS: " << checks
                  << " checks; 64 concurrent reserve contenders" << std::endl;
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "TJ.RemoteAccess Core tests FAIL after " << checks
                  << " checks: " << ex.what() << std::endl;
        return 1;
    }
}
