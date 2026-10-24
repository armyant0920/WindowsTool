using System.Diagnostics;
using System.Text;
using WindowsTool.Core.Rules;

namespace WindowsTool.Core.Execution;

public sealed record CommandResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>執行白名單命令（PLAN 10.3）。</summary>
public interface ICommandRunner
{
    bool IsAvailable(CommandCatalog.CommandInfo command);

    Task<CommandResult> RunAsync(CommandCatalog.CommandInfo command, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// 不經過 shell 直接啟動程式，參數全部是程式內建的常數。
/// Windows 上 <c>npm</c> 這類工具是 <c>.cmd</c>，必須透過 <c>cmd /d /c</c> 執行（參數仍然是固定的常數）。
/// </summary>
public sealed class CommandRunner : ICommandRunner
{
    public const int MaxOutputChars = 64 * 1024;

    public bool IsAvailable(CommandCatalog.CommandInfo command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Executable is not null && FindOnPath(command.Executable) is not null;
    }

    public async Task<CommandResult> RunAsync(CommandCatalog.CommandInfo command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Executable is null)
        {
            throw new InvalidOperationException($"{command.Id} 只提供操作指引，不會自動執行。");
        }

        var resolved = FindOnPath(command.Executable)
            ?? throw new FileNotFoundException($"找不到 {command.Executable}，請確認已經安裝並加入 PATH。");

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var extension = Path.GetExtension(resolved);
        if (OperatingSystem.IsWindows() && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(command.Executable);
        }
        else
        {
            start.FileName = resolved;
        }

        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutputChars)
                {
                    output.AppendLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 已經結束
            }

            if (!timedOut)
            {
                throw;
            }
        }

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        return new CommandResult(timedOut ? -1 : process.ExitCode, text, timedOut);
    }

    /// <summary>在 PATH 中尋找程式（Windows 依 PATHEXT 補上副檔名）。</summary>
    public static string? FindOnPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            foreach (var extension in Path.HasExtension(name) ? [""] : extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), name + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // PATH 中格式錯誤的項目
                }
            }
        }

        return null;
    }
}
