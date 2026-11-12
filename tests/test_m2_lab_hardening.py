# SPDX-License-Identifier: GPL-2.0-or-later
"""Source contracts for Lab link hardening. Functional transport tests are xUnit."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "TJRemoteAccess.Core"


class LabHardeningContracts(unittest.TestCase):
    def test_large_screen_packets_do_not_copy_payload_twice(self):
        protocol = (CORE / "LabProtocol.cs").read_text(encoding="utf-8")
        self.assertIn("MaxControlPacketBytes = 32", protocol)
        self.assertIn("maxPacketBytes", protocol)
        self.assertIn("ReadExactlyAsync(tag", protocol)
        self.assertIn("new byte[length - 1]", protocol)
        self.assertNotIn("buffer.AsSpan(1).ToArray()", protocol)
        self.assertIn("await stream.WriteAsync(header, stop)", protocol)
        self.assertIn("await stream.WriteAsync(body, stop)", protocol)
        self.assertNotIn("new byte[size + 4]", protocol)
        tests = (ROOT / "tests" / "lab_transport_limits_tests.cs").read_text(
            encoding="utf-8")
        self.assertIn("OversizedControlHeaderFailsBeforeAllocatingPayload", tests)
        self.assertIn("BlockingSocketReadCancelsWithoutAnotherNetworkPacket", tests)
        self.assertIn("FrameEncodingIsByteCompatibleWithPreviousLabProtocol", tests)
        self.assertIn("ConcurrentWritersCannotInterleaveHeaderAndBody", tests)

    def test_server_closes_idle_controller_and_caps_input_packets(self):
        server = (CORE / "LabServer.cs").read_text(encoding="utf-8")
        self.assertGreaterEqual(
            server.count("LabWire.MaxControlPacketBytes"), 2)
        self.assertIn(".WaitAsync(TimeSpan.FromSeconds(25), token)", server)
        self.assertIn(".WaitAsync(TimeSpan.FromSeconds(10), token)", server)
        self.assertIn("_sessions.Clear()", server)
        self.assertIn("_running = false", server)
        self.assertIn("_running = true", server)
        self.assertIn("if (!_running || token.IsCancellationRequested", server)
        self.assertIn("if (_controller == session)", server)
        # Authentication permits stay held until the Hello validation succeeds.
        self.assertGreater(
            server.index("_handshakeLimit.Release();"),
            server.index("if (hello.Type != LabMessage.Hello"))

    def test_viewer_has_read_deadline_and_bounded_paint_backlog(self):
        viewer = (CORE / "LabViewer.cs").read_text(encoding="utf-8")
        self.assertIn("frameDeadline.CancelAfter(TimeSpan.FromSeconds(25))", viewer)
        self.assertIn("Interlocked.CompareExchange(ref _frameUpdatePending", viewer)
        self.assertIn("Interlocked.Exchange(ref _frameUpdatePending", viewer)
        self.assertIn("bitmap.Dispose();", viewer)
        self.assertIn("ReferenceEquals(_tls, stream)", viewer)

    def test_certificate_probe_is_readonly_and_warns_before_expiry(self):
        script = (ROOT / "tools" / "Test-TJRemoteAccess-Lab.ps1").read_text(
            encoding="utf-8-sig")
        for keyword in ("Cert:\\CurrentUser\\My", "ExpiringSoon",
                        "ServerCertificateStatus", "ViewerCertificateStatuses",
                        "CertificateAttentionCount", "NoPrivateKey"):
            self.assertIn(keyword, script)
        for forbidden in ("New-SelfSignedCertificate", "Import-PfxCertificate",
                          "Remove-Item -Path Cert:", "New-NetFirewallRule",
                          "Set-NetFirewallRule", "New-Service"):
            self.assertNotIn(forbidden, script)


if __name__ == "__main__":
    unittest.main()
