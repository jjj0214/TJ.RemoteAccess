# SPDX-License-Identifier: GPL-2.0-or-later
"""TightVNC 2.8.90 parity contract; avoids claiming full RFB compatibility."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "TJRemoteAccess.Core"

class TightVncParityContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.viewer = (CORE / "LabViewer.cs").read_text(encoding="utf-8-sig")
        cls.server = (CORE / "LabServer.cs").read_text(encoding="utf-8-sig")
        cls.consent = (CORE / "LabClipboardConsent.cs").read_text(encoding="utf-8-sig")
        cls.transport = (CORE / "LabServerClipboardSession.cs").read_text(encoding="utf-8-sig")
        cls.clipboard = (CORE / "LabClipboardWire.cs").read_text(encoding="utf-8-sig")
        cls.protocol = (CORE / "LabProtocol.cs").read_text(encoding="utf-8-sig")

    def test_manual_full_refresh_rechecks_desktop_and_throttles_viewer(self):
        for item in ("LabMessage.RefreshFrame", "LabWire.ValidateRefreshFrame(packet.Payload)",
                     "_sessions.Contains(session)", "lastRefreshAt >= 800",
                     "M3InteractiveDesktopGuard.IsNormalInteractiveDesktop()",
                     "LabMessage.JpegFrame, latest.Jpeg"):
            self.assertIn(item, self.server)
        self.assertIn("RequestScreenRefreshAsync()", self.viewer)

    def test_viewer_has_menu_and_safe_shortcuts_not_secure_desktop_bypass(self):
        for item in ("InstallViewerMenu(toolbar)", "ShowConnectionInfo()",
                     "SendShortcutAsync", "ToggleModifierAsync",
                     "UpdateModifierMenus()", "_picture.LostFocus",
                     "_picture.Focused", "Hold Ctrl", "Hold Alt",
                     "Ctrl+Esc (Start)", "Alt+Tab", "Show &toolbar"):
            self.assertIn(item, self.viewer)
        self.assertNotIn("Clipboard.SetText(", self.server)
        self.assertIn("Secure desktop / UAC: Not supported", self.viewer)

    def test_clipboard_is_bounded_and_two_sided_consent_only(self):
        for item in ("MaxTextBytes = 16 * 1024", "StrictUtf8", "Invalid clipboard UTF-8",
                     "Clipboard text exceeds", "Clipboard text includes forbidden controls"):
            self.assertIn(item, self.clipboard)
        for item in ("MessageBoxDefaultButton.Button2", "Clipboard.SetText(text",
                     "Clipboard.GetText(", "authorized()", "TimeSpan.FromMinutes(2)"):
            self.assertIn(item, self.consent)
        for item in ("LabClipboardConsent.SetTextAsync", "LabClipboardConsent.GetTextAsync",
                     "_authorized()", "ClipboardDecision", "ClipboardText"):
            self.assertIn(item, self.transport)
        self.assertIn("LabServerClipboardSession.IsClipboardMessage", self.server)
        self.assertIn("Controller permission required for clipboard transfer", self.viewer)
        self.assertIn("Replace THIS PC's clipboard?", self.viewer)

    def test_multifile_upload_never_grants_batch_consent(self):
        for item in ("Multiselect = true", "pick.FileNames.Length > 32",
                     "foreach (string file in pick.FileNames)",
                     "await _transfers.UploadAsync(file, _stop.Token)"):
            self.assertIn(item, self.viewer)
        self.assertIn("LabTransferConsent.ApproveUploadAsync", (CORE / "LabServerTransferSession.cs").read_text(encoding="utf-8-sig"))

    def test_parity_map_marks_missing_and_excluded_features(self):
        doc = (ROOT / "docs/TIGHTVNC_2_8_90_FEATURE_PARITY_20261118_KO.md").read_text(encoding="utf-8")
        for item in ("ViewerMenu.cpp", "OptionsDialog.cpp", "FileTransferMainDialog.cpp",
                     "ServerConfig.h", "RemoteFileRenameOperation.cpp",
                     "미구현", "제외", "TLS", "Service 패키징 제외",
                     "2-PC", "24시간"):
            self.assertIn(item, doc)

if __name__ == "__main__":
    unittest.main()
