// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 TJ.RemoteAccess contributors
#include "secure_session_id.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#include <bcrypt.h>
#pragma comment(lib, "bcrypt.lib")
#endif

#include <array>
#include <cstdint>
#include <utility>

namespace tjra {

bool GenerateSecureSessionId(std::string* output) noexcept {
    if (!output) return false;
    output->clear();
#ifdef _WIN32
    std::array<UCHAR, 32> entropy{};
    const NTSTATUS status = BCryptGenRandom(
        nullptr, entropy.data(), static_cast<ULONG>(entropy.size()),
        BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (status < 0) {
        SecureZeroMemory(entropy.data(), entropy.size());
        return false;
    }

    constexpr char hex[] = "0123456789abcdef";
    try {
        std::string session_id;
        session_id.resize(entropy.size() * 2);
        for (std::size_t i = 0; i < entropy.size(); ++i) {
            session_id[i * 2] = hex[entropy[i] >> 4];
            session_id[i * 2 + 1] = hex[entropy[i] & 0x0F];
        }
        SecureZeroMemory(entropy.data(), entropy.size());
        *output = std::move(session_id);
        return true;
    } catch (...) {
        SecureZeroMemory(entropy.data(), entropy.size());
        return false;
    }
#else
    // No unspecified std::rand/std::random_device fallback is permitted.
    return false;
#endif
}

} // namespace tjra
