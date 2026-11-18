# SPDX-License-Identifier: GPL-2.0-or-later
"""Regression contract for the new interactive Preview UI, not end-to-end QA."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "TJRemoteAccess.Core"

class PreviewProductUXContracts(unittest.TestCase):
    def test_viewer_scale_freeze_snapshot_and_safe_remote_shortcut(self):
        source = (CORE / "LabViewer.cs").read_text(encoding="utf-8-sig")
        for requirement in (
            'new object[] { "Fit", "10%", "15%", "25%", "50%", "75%", "90%", "100%", "125%", "150%", "200%", "400%" }',
            "_canvasHost.AutoScroll = true",
            "_canvasHost.Controls.Add(_picture)",
            "_picture.SizeMode = PictureBoxSizeMode.Zoom",
            "_picture.SizeMode = PictureBoxSizeMode.StretchImage",
            "ApplyDisplayScale();",
            "if (_displayPaused) continue;",
            "if (!_displayPaused) await ReleaseHeldKeysAsync();",
            "private volatile bool _displayPaused;",
            "CanControl => _connected && !_displayPaused",
            "new SaveFileDialog",
            "System.Drawing.Imaging.ImageFormat.Png",
            "await SendAsync(LabMessage.Key, LabWire.Key(key, true))",
            "await SendAsync(LabMessage.Key, LabWire.Key(keys[i], false))",
            "(ushort)Keys.ControlKey, (ushort)Keys.ShiftKey, (ushort)Keys.Escape",
        ):
            self.assertIn(requirement, source)

    def test_server_access_settings_is_one_entry_without_erasing_password_policy(self):
        server = (CORE / "LabServer.cs").read_text(encoding="utf-8-sig")
        tray = (CORE / "LabServerTray.cs").read_text(encoding="utf-8-sig")
        access = (CORE / "LabServerClientAccessForm.cs").read_text(encoding="utf-8-sig")
        self.assertIn('Text = "Access & transfers"', server)
        self.assertIn('"Access settings..."', tray)
        self.assertIn("ShowPasswordAccess", server)
        self.assertIn('"Password / quick access..."', access)
        self.assertIn("LabPasswordAccess.NeedsListenerRestart", server)
        self.assertNotIn('_menu.Items.Add("Password access..."', tray)
        self.assertNotIn('controls.Controls.Add(_passwordButton)', server)

    def test_service_and_file_transfer_remain_unclaimed(self):
        server = (CORE / "LabServer.cs").read_text(encoding="utf-8-sig")
        source = (CORE / "LabProtocol.cs").read_text(encoding="utf-8-sig")
        self.assertIn("INTERACTIVE LAB ONLY", server)
        self.assertNotIn("FileTransferChunk", source)
        self.assertNotIn("FileTransferComplete", source)

if __name__ == "__main__":
    unittest.main()
