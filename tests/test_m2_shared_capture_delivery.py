# SPDX-License-Identifier: GPL-2.0-or-later
"""Static wiring regression: a single desktop capture producer serves up to three viewers."""
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "TJRemoteAccess.Core"


class LabSharedCaptureWiringTests(unittest.TestCase):
    def test_shared_frame_store_is_compiled_in_server_and_tests(self):
        source = {
            node.get("Include") for node in ET.parse(
                CORE / "TJ.RemoteAccess.Lab.Server.csproj").getroot().iter("Compile")
        }
        self.assertIn("LabSharedFrames.cs", source)
        suite = {
            node.get("Include") for node in ET.parse(
                ROOT / "tests" / "TJ.RemoteAccess.Lab.Tests.csproj").getroot().iter("Compile")
        }
        self.assertIn("lab_shared_frames_tests.cs", suite)
        self.assertIn(r"..\src\TJRemoteAccess.Core\LabSharedFrames.cs", suite)

    def test_only_one_server_capture_loop_owns_copy_from_screen(self):
        source = (CORE / "LabServer.cs").read_text(encoding="utf-8")
        self.assertIn("CaptureFramesShared(frames, run)", source)
        self.assertIn("frames.Publish(jpeg)", source)
        self.assertIn("SendFrames(tls, writeMutex, frames,", source)
        self.assertIn("var frame = frames.Latest", source)
        self.assertIn("frame.Sequence != lastSequence", source)
        self.assertIn("LabMessage.JpegFrame, frame.Jpeg, token", source)
        self.assertIn("_clientRoster.IsCertificateAllowed(clientCertificate)", source)
        self.assertIn("ReferenceEquals(_shutdown, run)", source)
        self.assertEqual(2, source.count("CaptureJpeg("))  # method and one producer call

    def test_shared_frame_references_are_volatile_and_bounded(self):
        source = (CORE / "LabSharedFrames.cs").read_text(encoding="utf-8")
        self.assertIn("Volatile.Read(ref _latest)", source)
        self.assertIn("Volatile.Write(ref _latest", source)
        self.assertIn("Interlocked.Increment(ref _sequence)", source)
        self.assertIn("jpeg.Length >= LabWire.MaxFrameBytes", source)
        self.assertNotIn("new Queue", source)
        self.assertNotIn(".ToArray()", source)
        tests = (ROOT / "tests" / "lab_shared_frames_tests.cs").read_text(
            encoding="utf-8")
        self.assertIn("ThreeReadersShareFrameReference", tests)
        self.assertIn("ConcurrentReadersAlwaysSeeCompleteFrame", tests)

    def test_readonly_performance_probe_samples_cpu_and_memory(self):
        script = (ROOT / "tools" / "Measure-TJRemoteAccess-Lab.ps1").read_text(
            encoding="utf-8-sig")
        for required in ("Get-Process", "WorkingSetMB", "PrivateMemoryMB",
                         "MachineCPUPercent", "DurationSeconds", "LogicalProcessors"):
            self.assertIn(required, script)
        for forbidden in ("Stop-Process", "Start-Process", "Set-Process",
                          "New-NetFirewallRule", "New-Service", "Set-Service",
                          "Set-ItemProperty", "New-Item", "Remove-Item"):
            self.assertNotIn(forbidden, script)


if __name__ == "__main__":
    unittest.main()
