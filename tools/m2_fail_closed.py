#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Fail-closed M2 transformations for verified, generated TightVNC GPL source.

These source changes are intentionally NOT applied to the original ZIP/tree.
They deny all legacy RFB/HTTP/Control listeners until a trusted authenticated
gateway and privileged input agent have been implemented and independently
reviewed. This does not make the fork production ready.
"""
from __future__ import annotations

from vendor_upstream import SourceImportError

# Signatures and following methods are pinned to the audited 2.8.90 source.
# A changed upstream structure fails generation rather than silently dropping
# a security fix. Each replacement retains the original function signature.
INTERLOCKS = {
    "tvnserver-app/TvnServer.cpp": (
        (
            "void TvnServer::restartMainRfbServer()",
            "void TvnServer::stopHttpServer()",
            """  stopMainRfbServer();
  m_log.message(_T("TJRA M2: legacy RFB listener disabled (no trusted gateway)"));""",
            "main-rfb-listener",
        ),
        (
            "void TvnServer::restartHttpServer()",
            "void TvnServer::restartControlServer()",
            """  stopHttpServer();
  m_log.message(_T("TJRA M2: legacy HTTP listener disabled"));""",
            "legacy-http-listener",
        ),
        (
            "void TvnServer::restartControlServer()",
            "void TvnServer::restartMainRfbServer()",
            """  stopControlServer();
  m_log.message(_T("TJRA M2: legacy untrusted control pipe disabled"));""",
            "legacy-control-pipe",
        ),
    ),
    "tvnserver-app/ExtraRfbServers.cpp": (
        (
            "bool ExtraRfbServers::reload(bool asService, RfbClientManager *mgr)",
            "void ExtraRfbServers::shutDown()",
            """  (void)asService;
  (void)mgr;
  shutDown();
  m_log->message(_T("TJRA M2: extra RFB listeners disabled"));
  return true;""",
            "extra-rfb-reload",
        ),
        (
            "bool ExtraRfbServers::startUp(bool asService, RfbClientManager *mgr)",
            "void ExtraRfbServers::getConfiguration(Conf *out)",
            """  (void)asService;
  (void)mgr;
  shutDown();
  return false;""",
            "extra-rfb-startup",
        ),
    ),
    "tvnserver-app/RfbClientManager.cpp": (
        (
            "Desktop *RfbClientManager::onClientAuth(RfbClient *client)",
            "bool RfbClientManager::onCheckForBan(RfbClient *client)",
            """  (void)client;
  // Legacy password success must NEVER be converted to a TJRA trusted identity.
  throw AuthException(_T("TJRA M2: session rejected (trusted gateway missing)"));""",
            "legacy-session-admission",
        ),
    ),
    "rfb-sconn/RfbClient.cpp": (
        (
            "void RfbClient::onKeyboardEvent(UINT32 keySym, bool down)",
            "void RfbClient::onMouseEvent(UINT16 x, UINT16 y, UINT8 buttonMask)",
            """  (void)keySym;
  (void)down;
  // TJRA M2: keyboard injection denied until trusted final injector exists.""",
            "legacy-keyboard-input",
        ),
        (
            "void RfbClient::onMouseEvent(UINT16 x, UINT16 y, UINT8 buttonMask)",
            "Rect RfbClient::getViewPortRect(const Dimension *fbDimension)",
            """  (void)x;
  (void)y;
  (void)buttonMask;
  // TJRA M2: pointer injection denied until trusted final injector exists.""",
            "legacy-pointer-input",
        ),
    ),
}
FILE_TRANSFER_BLOCK = (
    "    // FileTransfers initialization\n"
    "    if (config->isFileTransfersEnabled() &&\n"
    "        rfbInitializer.getTightEnabledFlag()) {\n"
    "      fileTransfer = new FileTransferRequestHandler(&codeRegtor, &output, m_desktop, m_log, !m_viewOnly);\n"
    "      m_log->debug(_T(\"File transfer has been created\"));\n"
    "    } else {\n"
    "      m_log->info(_T(\"File transfer is not allowed\"));\n"
    "    }\n"
)

EXPECTED_IDS = {entry[3] for group in INTERLOCKS.values() for entry in group} | {"legacy-rfb-file-transfer"}


def _replace_method(text: str, signature: str, following: str,
                    body: str, relative: str) -> str:
    if text.count(signature) != 1 or text.count(following) != 1:
        raise SourceImportError(
            f"Unexpected {relative} structure for security interlock: {signature}")
    nl = "\r\n" if "\r\n" in text else "\n"
    start = text.index(signature)
    next_method = text.index(following, start + len(signature))
    opening = signature + nl + "{"
    if text[start:start + len(opening)] != opening:
        raise SourceImportError(f"Unexpected method opening in {relative}: {signature}")
    closing = nl + "}" + nl
    close = text.rfind(closing, start + len(opening), next_method)
    if close < 0 or text[close + len(closing):next_method].strip():
        raise SourceImportError(f"Unexpected method end in {relative}: {signature}")
    replacement = opening + nl + body.replace("\n", nl) + nl + "}" + nl
    return text[:start] + replacement + text[close + len(closing):]


def apply_interlocks(relative: str, source: str) -> tuple[str, list[str]]:
    applied: list[str] = []
    result = source
    for signature, following, body, interlock_id in INTERLOCKS.get(relative, ()):
        result = _replace_method(result, signature, following, body, relative)
        applied.append(interlock_id)
    if relative == "rfb-sconn/RfbClient.cpp":
        nl = "\r\n" if "\r\n" in result else "\n"
        original = FILE_TRANSFER_BLOCK.replace("\n", nl)
        if result.count(original) != 1:
            raise SourceImportError("Unexpected legacy file-transfer block")
        disabled = (
            "    // TJRA M2: legacy RFB file transfer extension disabled." + nl
            + '    m_log->info(_T("TJRA M2: RFB file transfer disabled"));'+nl
        )
        result = result.replace(original, disabled, 1)
        applied.append("legacy-rfb-file-transfer")
    return result, applied
