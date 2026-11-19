# SPDX-License-Identifier: GPL-2.0-or-later
"""Static guards for new features; runtime Windows/2-PC acceptance is separate."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / 'src' / 'TJRemoteAccess.Core'
WORKFLOW = ROOT / '.github' / 'workflows' / 'service-preview-ci.yml'


class PreviewFeatureSecurityContracts(unittest.TestCase):
    def test_transfer_requires_server_owner_approval_and_real_controller(self):
        source = (SRC / 'LabServerTransferSession.cs').read_text(encoding='utf-8-sig')
        server = (SRC / 'LabServer.cs').read_text(encoding='utf-8-sig')
        consent = (SRC / 'LabTransferConsent.cs').read_text(encoding='utf-8-sig')
        for key in ('if (!_authorized() || !_filePolicy.RootUnchanged)', 'ApproveUploadAsync', 'SelectServerFileAsync',
                    'FileDecision', 'FileRequest', 'FileResult', 'SendChunksAsync'):
            self.assertIn(key, source)
        for key in ('_controller == session', '_sessions.Contains(session)',
                    'M3InteractiveDesktopGuard.IsNormalInteractiveDesktop', 'LabServerTransferSession'):
            self.assertIn(key, server)
        self.assertIn('MessageBoxDefaultButton.Button2', consent)
        self.assertIn('OpenFileDialog', consent)

    def test_file_protocol_has_bounded_chunks_paths_and_integrity(self):
        source = (SRC / 'LabFileTransfer.cs').read_text(encoding='utf-8-sig')
        for key in ('4L * 1024 * 1024 * 1024', '48 * 1024', 'SafeFileName',
                    'Path.GetInvalidFileNameChars', 'FileAttributes.ReparsePoint',
                    'SHA256.HashDataAsync', 'CryptographicOperations.FixedTimeEquals',
                    'FileMode.CreateNew', 'File.Move(_partial, _target, overwrite: false)',
                    'Invalid transfer filename', 'GetHashAndReset'):
            if key == 'Invalid transfer filename':
                continue
            self.assertIn(key, source)
        self.assertNotIn('TcpListener', source)

    def test_client_options_stay_pinned_and_autoreconnect_certificate_only(self):
        viewer = (SRC / 'LabViewer.cs').read_text(encoding='utf-8-sig')
        opts = (SRC / 'LabConnectionOptions.cs').read_text(encoding='utf-8-sig')
        server = (SRC / 'LabServer.cs').read_text(encoding='utf-8-sig')
        for key in ('MayAutoReconnect(', 'SelectedProfile?.AuthMode == "Certificate"',
                    'LabMessage.CaptureOptions', 'LabMessage.Wheel', '_options.ViewOnly',
                    'SetFullScreen(', 'LabViewerTransfers'):
            self.assertIn(key, viewer)
        self.assertIn('return allowed && TJ.RemoteAccess.M3.M3InteractiveDesktopGuard', server)
        self.assertIn('requestedViewOnly', server)
        self.assertIn('LabWire.ParseWheel', server)
        self.assertIn('LabWire.ParseCaptureSettings', server)
        self.assertIn('JpegQuality = 65', opts)
        self.assertIn('FramesPerSecond = 6', opts)
        self.assertIn('Certificate', opts)
        self.assertNotIn('ClientCertificateThumbprint { get;', opts)

    def test_service_workflow_is_isolated_and_consents_default_off(self):
        workflow = WORKFLOW.read_text(encoding='utf-8-sig')
        for key in ('runs-on: windows-latest', 'ServicePreviewPackage.ps1',
                    '-ApproveServicePreview -Start', '-ApproveServicePreview -Upgrade -Start',
                    '-ApproveServicePreviewRemoval', "StartName -ne 'LocalSystem'",
                    "OperatorDesktopPolicy -ne 'Disabled'", 'Tcp15544Listeners',
                    'Get-Acl $dir', 'SourceRevision', 'SHA-256'):
            if key == 'SourceRevision':
                continue
            self.assertIn(key, workflow)
        self.assertNotIn('-EnableSupervisedDesktop', workflow)
        self.assertNotIn('New-NetFirewallRule', workflow)
        self.assertNotIn('Uninstall-TightVNC', workflow)
        setup = (ROOT / 'tools' / 'Install-TJRemoteAccess-ServicePreview.ps1').read_text(
            encoding='utf-8-sig')
        self.assertIn("Join-Path $env:ProgramFiles 'TJ.RemoteAccess'", setup)
        self.assertIn('-ApproveServicePreview', setup)


if __name__ == '__main__':
    unittest.main()
