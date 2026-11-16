// SPDX-License-Identifier: GPL-2.0-or-later
// Double-clickable, self-contained WinForms setup window.
// Users never need PowerShell or a .NET SDK. An offline Preview ZIP and its
// matching SHA-256 sidecar must be beside this EXE (or selected via Browse).
using System.Drawing;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Installer;

internal static class PreviewInstallerProgram
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new PreviewInstallerForm());
    }
}

internal sealed class PreviewInstallerForm : Form
{
    private readonly PreviewPackageInstaller _engine = new();
    private readonly TextBox _path = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox _sha = new() { Dock = DockStyle.Fill, MaxLength = 64,
                                            CharacterCasing = CharacterCasing.Upper };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList,
                                               Dock = DockStyle.Fill };
    private readonly Button _install = new() { Text = "Install / Update",
                                               Width = 146, Height = 36 };
    private readonly Button _uninstall = new() { Text = "Remove programs",
                                                 Width = 146, Height = 36 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90, Height = 36,
                                              Enabled = false };
    private readonly Button _browse = new() { Text = "Browse...", Width = 100,
                                              Height = 30 };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100,
                                                Value = 0, Dock = DockStyle.Fill };
    private readonly Label _status = new() { Text = "Ready", AutoSize = true,
                                             Dock = DockStyle.Fill };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true,
                                           ScrollBars = ScrollBars.Vertical,
                                           Dock = DockStyle.Fill };
    private CancellationTokenSource? _cancellation;
    private bool _working;
    private string? _lastPhase;
    private readonly string _logPath;

    internal PreviewInstallerForm()
    {
        Text = "TJ.RemoteAccess Setup - 0.5.3 Preview";
        Width = 740;
        Height = 640;
        MinimumSize = new Size(650, 545);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F);
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TJ.RemoteAccess", "InstallerLogs",
            "Installer-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16),
            ColumnCount = 1, RowCount = 9 };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new Label {
            Text = "TJ.RemoteAccess   |   Offline Installer",
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        };
        main.Controls.Add(heading, 0, 0);
        main.Controls.Add(new Label {
            Text = "Current-user Preview only. Server / Viewer / Setup. " +
                   "No admin rights or PowerShell required. " +
                   "Unsigned installer: verify package SHA-256 from a trusted source.",
            Dock = DockStyle.Fill, AutoSize = false
        }, 0, 1);

        var fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fileRow.Controls.Add(_path, 0, 0);
        fileRow.Controls.Add(_browse, 1, 0);
        main.Controls.Add(fileRow, 0, 2);

        var hashRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        hashRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        hashRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hashRow.Controls.Add(new Label {
            Text = "Package SHA-256", AutoSize = true, Anchor = AnchorStyles.Left
        }, 0, 0);
        hashRow.Controls.Add(_sha, 1, 0);
        main.Controls.Add(hashRow, 0, 3);

        var modeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeRow.Controls.Add(new Label {
            Text = "Install components", AutoSize = true, Anchor = AnchorStyles.Left
        }, 0, 0);
        _mode.Items.AddRange(["Both (Server + Viewer + Setup)",
                              "Server + Setup", "Viewer + Setup"]);
        _mode.SelectedIndex = 0;
        modeRow.Controls.Add(_mode, 1, 0);
        main.Controls.Add(modeRow, 0, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill,
                                            FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.Add(_install);
        actions.Controls.Add(_uninstall);
        actions.Controls.Add(_cancel);
        main.Controls.Add(actions, 0, 5);
        main.Controls.Add(_status, 0, 6);
        main.Controls.Add(_bar, 0, 7);
        main.Controls.Add(_log, 0, 8);
        Controls.Add(main);

        _browse.Click += (_, _) => Browse();
        _install.Click += async (_, _) => await RunAsync(remove: false);
        _uninstall.Click += async (_, _) => await RunAsync(remove: true);
        _cancel.Click += (_, _) => {
            _cancel.Enabled = false;
            _cancellation?.Cancel();
            Append("Cancellation requested; verified installed files will be restored if swapping.");
        };
        FormClosing += (_, e) => {
            if (_working)
            {
                e.Cancel = true;
                _cancellation?.Cancel();
                Append("Close requested during installation; waiting for safe cancellation.");
            }
        };
        DetectAdjacentPackage();
        Append("Setup UI ready. This installer does NOT run PowerShell.");
        Append("Install folder: " + _engine.InstallRoot);
    }

    private PreviewInstallMode SelectedMode => _mode.SelectedIndex switch
    {
        1 => PreviewInstallMode.Server,
        2 => PreviewInstallMode.Viewer,
        _ => PreviewInstallMode.Both
    };

    private void DetectAdjacentPackage()
    {
        try
        {
            var folder = AppContext.BaseDirectory;
            var packages = Directory.GetFiles(folder, "TJ.RemoteAccess-*-win-x64.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
            if (packages.Length == 1) SetPackage(packages[0]);
            else if (packages.Length > 1)
                Append("Multiple offline packages found. Choose one with Browse.");
            else Append("Select a TJ.RemoteAccess offline ZIP with Browse.");
        }
        catch (Exception error) { Append("Package discovery: " + error.Message); }
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog {
            Title = "Select TJ.RemoteAccess Preview offline package",
            Filter = "TJ.RemoteAccess ZIP package (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetPackage(dialog.FileName);
    }

    private void SetPackage(string path)
    {
        _path.Text = path;
        try
        {
            _sha.Text = PreviewPackageInstaller.ReadSidecarHash(path) ?? "";
            Append(_sha.Text.Length == 64
                ? "SHA-256 sidecar read. Verify this value against a trusted independent source."
                : "No .sha256 sidecar found. Enter the 64-character verified package SHA-256.");
        }
        catch (Exception error)
        {
            _sha.Clear();
            Append("Package sidecar rejected: " + error.Message);
        }
    }

    private async Task RunAsync(bool remove)
    {
        if (_working) return;
        string package = _path.Text.Trim();
        string sha = _sha.Text.Trim();
        if (!remove && (!File.Exists(package) || !PreviewPackageInstaller.IsSha256(sha)))
        {
            MessageBox.Show(this, "Select the offline ZIP and supply its valid SHA-256.",
                "Missing verified package", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (remove && MessageBox.Show(this,
            "Remove selected TJ.RemoteAccess programs for the CURRENT USER?\n" +
            "Saved TLS certificates, passwords, and profiles will be preserved.\n" +
            "Any scheduled auto-start task must be disabled separately.",
            "Confirm removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        _working = true;
        _lastPhase = null;
        _bar.Value = 0;
        _install.Enabled = false;
        _uninstall.Enabled = false;
        _browse.Enabled = false;
        _mode.Enabled = false;
        _sha.Enabled = false;
        _cancel.Enabled = true;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var mode = SelectedMode;
        var report = new Progress<PreviewInstallProgress>(step => {
            if (IsDisposed) return;
            _bar.Value = Math.Clamp(step.Percent, _bar.Minimum, _bar.Maximum);
            _status.Text = step.Phase + "  (" + step.Percent + "%)";
            if (!string.Equals(_lastPhase, step.Phase, StringComparison.Ordinal))
            {
                _lastPhase = step.Phase;
                Append(step.Phase + ": " + step.Detail);
            }
        });
        try
        {
            if (remove)
                await Task.Run(() => _engine.Remove(mode, report, token), token);
            else
                await Task.Run(() => _engine.Install(package, sha, mode, report, token), token);
            _bar.Value = 100;
            _status.Text = remove ? "Removal complete" : "Installation complete";
            Append(_status.Text + ". Configuration and certificates preserved.");
            MessageBox.Show(this,
                (remove ? "Removal" : "Installation") +
                " completed.\n\nLogs: " + _logPath,
                "TJ.RemoteAccess Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled";
            Append("Cancelled. Files already installed were rolled back if a swap had started.");
        }
        catch (Exception error)
        {
            _status.Text = "FAILED";
            Append("FAILED: " + error);
            MessageBox.Show(this, error.Message + "\n\nSee setup log: " + _logPath,
                "TJ.RemoteAccess Setup FAILED", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _working = false;
            _install.Enabled = true;
            _uninstall.Enabled = true;
            _browse.Enabled = true;
            _mode.Enabled = true;
            _sha.Enabled = true;
            _cancel.Enabled = false;
        }
    }

    private void Append(string message)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
        _log.AppendText(line + Environment.NewLine);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
