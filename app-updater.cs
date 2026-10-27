using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WechatDictationProbe;

internal static class AppUpdater
{
    private const string RepoOwner = "lsby";
    private const string RepoName = "wechat-dictation-bridge";
    private const string ApiLatestUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";

    /// <summary>
    /// 在程序启动后后台延时清理上次遗留的旧版本备份文件（.bak）。
    /// </summary>
    public static void CleanupOldBackups()
    {
        Task.Run(async () =>
        {
            // 等待 1.5 秒，让老进程彻底退出并释放文件占用
            await Task.Delay(1500);

            try
            {
                var appDir = AppContext.BaseDirectory;
                if (!Directory.Exists(appDir)) return;

                var bakFiles = Directory.GetFiles(appDir, "*.bak");
                foreach (var bakFile in bakFiles)
                {
                    for (int retry = 0; retry < 5; retry++)
                    {
                        try
                        {
                            if (File.Exists(bakFile))
                            {
                                File.Delete(bakFile);
                            }
                            break;
                        }
                        catch
                        {
                            await Task.Delay(500);
                        }
                    }
                }
            }
            catch
            {
                // 忽略清理异常
            }
        });
    }

    /// <summary>
    /// 获取当前程序规范化后的版本号。
    /// </summary>
    public static Version GetCurrentVersion()
    {
        var rawVersion = Application.ProductVersion;
        // 去除构建元数据，例如 0.0.1+abc1234
        var clean = rawVersion.Split('+')[0].Trim();
        if (Version.TryParse(clean, out var version))
        {
            return version;
        }

        return new Version(0, 0, 1);
    }

    /// <summary>
    /// 触发检查更新流程。
    /// </summary>
    public static async Task CheckForUpdatesAsync(IWin32Window? owner, bool silentIfLatest = false)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("wechat-dictation-bridge", GetCurrentVersion().ToString()));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(ApiLatestUrl, cts.Token);
            }
            catch (Exception ex)
            {
                ShowErrorWithLink(owner, $"检查更新失败，无法连接到 GitHub：\r\n{ex.Message}");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                ShowErrorWithLink(owner, $"检查更新失败，GitHub 返回状态码：{(int)response.StatusCode} {response.ReasonPhrase}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            var releaseUrl = root.TryGetProperty("html_url", out var htmlElem) ? htmlElem.GetString() ?? ReleasesPageUrl : ReleasesPageUrl;
            var body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";

            var remoteVersion = ParseVersionFromTag(tagName);
            var currentVersion = GetCurrentVersion();

            if (remoteVersion == null || remoteVersion <= currentVersion)
            {
                if (!silentIfLatest)
                {
                    MessageBox.Show(
                        owner,
                        $"当前已是最新版本 (v{currentVersion})，无需更新～",
                        "检查更新",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return;
            }

            // 查找可供下载的 Windows x64 可执行程序 asset
            string? downloadUrl = null;
            string? sha256Url = null;
            string? assetName = null;

            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = url;
                        assetName = name;
                    }
                    else if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        sha256Url = url;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                var askOpen = MessageBox.Show(
                    owner,
                    $"发现新版本 {tagName}，但 Release 中未找到对应的 win-x64 可执行程序。\r\n\r\n是否前往 GitHub 页面查看？",
                    "发现新版本",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (askOpen == DialogResult.Yes)
                {
                    OpenBrowser(releaseUrl);
                }
                return;
            }

            var changelog = string.IsNullOrWhiteSpace(body) ? "(无更新日志)" : body.Trim();
            if (changelog.Length > 500)
            {
                changelog = changelog[..500] + "...";
            }

            var confirmResult = MessageBox.Show(
                owner,
                $"发现新版本：{tagName}（当前版本：v{currentVersion}）\r\n\r\n更新说明：\r\n{changelog}\r\n\r\n是否立即下载并更新？",
                "检查更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirmResult != DialogResult.Yes)
            {
                return;
            }

            // 执行下载与自我替换流程
            await DownloadAndApplyUpdateAsync(owner, downloadUrl, sha256Url, assetName ?? "update.exe", tagName);
        }
        catch (Exception ex)
        {
            ShowErrorWithLink(owner, $"检查更新过程中发生未预期异常：\r\n{ex.Message}");
        }
    }

    private static async Task DownloadAndApplyUpdateAsync(
        IWin32Window? owner,
        string downloadUrl,
        string? sha256Url,
        string assetFileName,
        string tagName)
    {
        using var progressDialog = new UpdateProgressForm(tagName);
        progressDialog.Show(owner);

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            MessageBox.Show(
                owner,
                "无法获取当前程序的可执行文件路径，无法自动完成更新。",
                "更新失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var appDir = AppContext.BaseDirectory;
        var tempFile = Path.Combine(appDir, $"update-{tagName}.tmp");
        var backupExe = currentExe + ".bak";

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("wechat-dictation-bridge", GetCurrentVersion().ToString()));

            // 1. 下载更新包
            progressDialog.UpdateStatus("正在从 GitHub 下载新版本...");
            using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, progressDialog.CancellationToken))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var contentStream = await response.Content.ReadAsStreamAsync(progressDialog.CancellationToken);
                await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, progressDialog.CancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), progressDialog.CancellationToken);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        var percentage = (int)((totalRead * 100) / totalBytes);
                        progressDialog.SetProgress(percentage, $"已下载 {totalRead / 1024 / 1024:F1} MB / {totalBytes / 1024 / 1024:F1} MB ({percentage}%)");
                    }
                    else
                    {
                        progressDialog.UpdateStatus($"已下载 {totalRead / 1024 / 1024:F1} MB");
                    }
                }
            }

            // 2. 校验 SHA-256（如果有提供 sha256 文件）
            if (!string.IsNullOrEmpty(sha256Url))
            {
                progressDialog.UpdateStatus("正在校验文件哈希值 (SHA-256)...");
                try
                {
                    var sha256Content = await client.GetStringAsync(sha256Url, progressDialog.CancellationToken);
                    var expectedHash = ExtractHash(sha256Content);
                    if (!string.IsNullOrEmpty(expectedHash))
                    {
                        var actualHash = await ComputeSha256Async(tempFile, progressDialog.CancellationToken);
                        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"SHA-256 校验不匹配！\r\n期望：{expectedHash}\r\n实际：{actualHash}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    // 若 sha256 校验网络获取失败，只记录但不阻断
                    Debug.WriteLine($"SHA-256 校验跳过: {ex.Message}");
                }
            }

            progressDialog.UpdateStatus("下载校验完成，准备应用更新...");

            // 3. 执行重命名备份与替换
            // 规范化目标可执行文件名：统一为无版本号的标准文件名 (wechat-dictation-bridge.exe)
            var targetExe = Path.Combine(appDir, "wechat-dictation-bridge.exe");

            // 先尝试移除已有的 .bak 备份
            if (File.Exists(backupExe))
            {
                try
                {
                    File.Delete(backupExe);
                }
                catch
                {
                    backupExe = $"{currentExe}.{DateTime.Now:yyyyMMddHHmmss}.bak";
                }
            }

            // 如果 targetExe 存在且与当前进程不同，先清理 targetExe 避免冲突
            if (!string.Equals(targetExe, currentExe, StringComparison.OrdinalIgnoreCase) && File.Exists(targetExe))
            {
                try
                {
                    File.Delete(targetExe);
                }
                catch { }
            }

            // 将当前正在运行的 exe 重命名为 .bak
            File.Move(currentExe, backupExe);

            try
            {
                // 将下载的 tmp 文件重命名为标准 targetExe
                File.Move(tempFile, targetExe);
            }
            catch (Exception ex)
            {
                // 如果移动新文件失败，立刻把旧版本移回来以确保回档！
                if (File.Exists(backupExe) && !File.Exists(currentExe))
                {
                    File.Move(backupExe, currentExe);
                }
                throw new InvalidOperationException($"写入新版本文件失败，已安全回滚到旧版本：{ex.Message}", ex);
            }

            // 4. 拉起新版本进程
            progressDialog.UpdateStatus("更新成功！正在重启启动新版本...");
            Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                UseShellExecute = true
            });

            // 5. 退出当前程序
            Application.Exit();
        }
        catch (OperationCanceledException)
        {
            // 用户在进度窗口点击了取消
            CleanFileSafe(tempFile);
            MessageBox.Show(owner, "用户已取消下载更新。", "更新已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            CleanFileSafe(tempFile);
            ShowErrorWithLink(owner, $"更新失败：\r\n{ex.Message}");
        }
    }

    private static void CleanFileSafe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略临时文件删除异常
        }
    }

    private static string? ExtractHash(string sha256Content)
    {
        // sha256 格式通常是: <hash>  <filename> 或仅有 <hash>
        var match = Regex.Match(sha256Content, @"\b([a-fA-F0-9]{64})\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static Version? ParseVersionFromTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var cleanTag = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(cleanTag, out var version) ? version : null;
    }

    private static void ShowErrorWithLink(IWin32Window? owner, string message)
    {
        var result = MessageBox.Show(
            owner,
            $"{message}\r\n\r\n是否前往 GitHub Release 网页手动下载？",
            "检查更新提示",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            OpenBrowser(ReleasesPageUrl);
        }
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开浏览器失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 下载进度显示对话框。
/// </summary>
internal sealed class UpdateProgressForm : Form
{
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _cancelButton;
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken CancellationToken => _cts.Token;

    public UpdateProgressForm(string tagName)
    {
        Text = $"正在更新至 {tagName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Width = 420;
        Height = 170;
        Font = new Font("Microsoft YaHei UI", 9F);

        _statusLabel = new Label
        {
            Text = "准备下载...",
            Location = new Point(20, 20),
            Width = 360,
            Height = 24,
            AutoEllipsis = true
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(20, 50),
            Width = 360,
            Height = 22,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous
        };

        _cancelButton = new Button
        {
            Text = "取消",
            Location = new Point(300, 85),
            Width = 80,
            Height = 30
        };
        _cancelButton.Click += (_, _) =>
        {
            _cts.Cancel();
            _cancelButton.Enabled = false;
            _statusLabel.Text = "正在取消下载...";
        };

        Controls.Add(_statusLabel);
        Controls.Add(_progressBar);
        Controls.Add(_cancelButton);

        FormClosing += (_, e) =>
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        };
    }

    public void UpdateStatus(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateStatus(message));
            return;
        }

        _statusLabel.Text = message;
    }

    public void SetProgress(int percentage, string detail)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetProgress(percentage, detail));
            return;
        }

        _progressBar.Value = Math.Clamp(percentage, 0, 100);
        _statusLabel.Text = detail;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
