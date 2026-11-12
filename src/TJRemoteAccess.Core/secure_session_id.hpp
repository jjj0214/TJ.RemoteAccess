// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 TJ.RemoteAccess contributors
#pragma once

#include <string>

namespace tjra {

// Windows OS CSPRNG, 256-bit unpredictable identifier encoded as 64 lowercase
// hexadecimal characters. The result is an opaque session identifier, NOT
// a certificate, encryption key or proof of authenticated peer identity.
// No weak pseudo-random fallback: returns false if unavailable.
bool GenerateSecureSessionId(std::string* output) noexcept;

} // namespace tjra
