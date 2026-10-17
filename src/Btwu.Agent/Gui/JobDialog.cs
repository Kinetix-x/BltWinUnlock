using System.Diagnostics;
using System.Text;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>
/// Runs one job in a child process and streams its transcript into the window.
///
/// The transcript is a file, not a pipe, because the elevated jobs are started with
/// <c>ShellExecute</c> and <c>ShellExecute</c> cannot hand back a pipe. That constraint shapes the
/// whole class: completion is detected by watching for <see cref="GuiEntry.JobDonePrefix"/> in the
/// file, the exit code is parsed out of the same line, and the process handle is only ever used as a
/// fallback signal.
///
/// The tail has a deadline for the same reason. A child that is killed outright - by the installer's
/// own "stop every btwu process" step, say - writes no final line, and a window that waits forever on
/// a job that is already over is worse than one that gives up and says so.
/// </summary>
sealed class JobDialog : Form
{
    const int TailIntervalMs = 200;
    static readonly TimeSpan Deadline = TimeSpan.FromMinutes(20);

    readonly string _path;
    readonly RichTextBox _transcript;
    readonly Label _summary;
    readonly Button _close;
    readonly Button _openLog;
    readonly System.Windows.Forms.Timer _tail = new() { Interval = TailIntervalMs };
    readonly Func<(Process? process, string? error)> _start;
    readonly DateTime _startedAt = DateTime.UtcNow;

    Process? _process;
    long _offset;
    bool _finished;
    int? _exitCode;

    /// <summary>Artifacts the job announced with a <c>BTWU-ARTIFACT</c> line, in the order printed.</summary>
    JobDialog(string title, string description, string logPath, Func<(Process?, string?)> start)
    {
        _path = logPath;
        _start = start;

        Text = title;

        // Same rule as the two other windows: hand-written sizes are not DPI-scaled, so they go
        // through Ui.Px, and every row is sized by its content. This dialog had fixed heights
        // (a 58px description band, a 46px button row) and at 200% scaling it cut the description's
        // second line off the top and sliced the buttons in half along the bottom.
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(Ui.Px(480), Ui.Px(320));
        Size = new Size(Ui.Px(700), Ui.Px(520));
        Font = new Font("Microsoft YaHei UI", 9f);
        ShowInTaskbar = false;
        MinimizeBox = false;

        var header = new Label
        {
            AutoSize = true,
            Padding = Ui.Pad(4, 4, 4, 8),
            Text = description,
        };

        _transcript = new RichTextBox
        {
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(250, 250, 250),
            BorderStyle = BorderStyle.FixedSingle,
            DetectUrls = false,
        };

        _summary = new Label
        {
            AutoSize = true,
            Padding = Ui.Pad(4, 6, 4, 4),
            Text = "正在启动…",
        };

        _close = Ui.Action("关闭", Close);
        _openLog = Ui.Action("打开完整日志", () => Shell.Open(_path));
        _close.Enabled = false;
        _openLog.Enabled = false;
        var copy = Ui.Action("复制全部", () =>
        {
            try
            {
                if (_transcript.TextLength > 0) Clipboard.SetText(_transcript.Text);
            }
            catch (Exception ex)
            {
                Log.Warn($"cannot copy the transcript: {ex.Message}");
            }
        });

        // Right-to-left so 关闭 lands in the corner Windows users expect; AutoSize captions because a
        // fixed width is what clipped them before.
        var buttons = Ui.ButtonRow(_close, _openLog, copy);
        buttons.FlowDirection = FlowDirection.RightToLeft;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = Ui.Pad(10, 10, 10, 10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // description
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // transcript
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // summary
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons
        root.RowCount = 4;
        header.Dock = DockStyle.Fill;
        _transcript.Dock = DockStyle.Fill;
        _summary.Dock = DockStyle.Fill;
        buttons.Dock = DockStyle.Fill;
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_transcript, 0, 1);
        root.Controls.Add(_summary, 0, 2);
        root.Controls.Add(buttons, 0, 3);

        Controls.Add(root);

        CancelButton = _close;
        _tail.Tick += (_, _) => Pump();
        Shown += (_, _) =>
        {
            Ui.FitToScreen(this);
            Begin();
        };
        FormClosing += OnFormClosing;
    }

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_finished || _process is null) return;

        var answer = MessageBox.Show(this,
            "任务还在运行。现在关掉窗口不会终止它——它会在后台继续。\n\n要关闭这个窗口吗？",
            "Btwu", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) e.Cancel = true;
    }

    void Begin()
    {
        var (process, error) = _start();
        if (process is null)
        {
            _summary.Text = "未能启动";
            Append($"{error}\n");
            Finish(null);
            return;
        }

        _process = process;
        _summary.Text = $"正在运行（pid {process.Id}）…";
        _tail.Start();
    }

    /// <summary>Reads whatever has been appended to the transcript since the last tick.</summary>
    void Pump()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _offset) _offset = 0;
                if (stream.Length > _offset)
                {
                    stream.Seek(_offset, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    Append(reader.ReadToEnd());
                    _offset = stream.Length;
                }
            }
        }
        catch (IOException) { /* the writer rotates or the file is being created - try again next tick */ }
        catch (UnauthorizedAccessException) { }

        var done = GuiEntry.JobDonePrefix is { Length: > 0 } prefix && RawText.Contains(prefix);
        if (done)
        {
            Finish(_exitCode ?? ParseExitCode());
            return;
        }

        if (_process is not null)
        {
            try
            {
                if (_process.HasExited)
                {
                    // The child is gone. Give its stream one more tick to land, then conclude.
                    for (var i = 0; i < 3 && !RawText.Contains(GuiEntry.JobDonePrefix); i++) Thread.Sleep(TailIntervalMs);
                    Finish(_process.ExitCode);
                    return;
                }
            }
            catch (InvalidOperationException) { }
        }

        if (DateTime.UtcNow - _startedAt > Deadline)
            Finish(null);
    }

    readonly StringBuilder _raw = new();
    string RawText => _raw.ToString();

    int? ParseExitCode()
    {
        var index = RawText.LastIndexOf(GuiEntry.JobDonePrefix, StringComparison.Ordinal);
        if (index < 0) return null;
        var rest = RawText[(index + GuiEntry.JobDonePrefix.Length)..];
        var end = rest.IndexOf(' ');
        var token = end < 0 ? rest : rest[..end];
        return int.TryParse(token.Trim(), out var code) ? code : null;
    }

    void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // _raw is what the completion marker is searched for in; the RichTextBox is only what the
        // person sees. Dropping this line silently breaks "job finished" detection - the window would
        // then only notice via HasExited.
        _raw.Append(text);

        _transcript.AppendText(text);
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.ScrollToCaret();
    }

    void Finish(int? exitCode)
    {
        if (_finished) return;
        _finished = true;
        _exitCode = exitCode;

        _tail.Stop();
        Pump();

        if (exitCode is null)
            _summary.Text = "任务没有留下结束标记——可能被强制结束（安装程序会停掉所有 btwu 进程）。详见上面的输出。";
        else if (exitCode == 0)
            _summary.Text = "完成（退出码 0）";
        else
            _summary.Text = $"结束，但有错误（退出码 {exitCode}）";

        _close.Enabled = true;
        _openLog.Enabled = File.Exists(_path);
    }

    /// <summary>
    /// Shows the dialog, runs <paramref name="start"/> once it is on screen, and reports what happened.
    ///
    /// The result is a plain record rather than the dialog itself: the form is disposed the moment
    /// this returns, and handing callers a disposed control to read from is how "why is my summary
    /// blank" bugs are born.
    /// </summary>
    public static JobResult Show(
        IWin32Window? owner,
        string title,
        string description,
        string logPath,
        Func<(Process?, string?)> start)
    {
        using var dialog = new JobDialog(title, description, logPath, start);
        if (owner is null) dialog.ShowDialog(); else dialog.ShowDialog(owner);

        return new JobResult(
            dialog._finished,
            dialog._exitCode,
            File.Exists(logPath) ? logPath : null);
    }
}

/// <summary>Outcome of one <see cref="JobDialog"/> run. Survives the dialog being disposed.</summary>
sealed record JobResult(bool Finished, int? ExitCode, string? LogPath)
{
    /// <summary>Finished and reported success.</summary>
    public bool Succeeded => Finished && ExitCode == 0;

    /// <summary>How to describe this in one line, for the caller's status bar.</summary>
    public string Describe() => !Finished
        ? "没有拿到结束标记"
        : ExitCode == 0 ? "成功" : $"退出码 {ExitCode}";
}
