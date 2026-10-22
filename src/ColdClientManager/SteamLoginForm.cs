using SteamKit2.Authentication;

namespace ColdClientManager;

public sealed class SteamLoginForm : Form, IAuthenticator
{
    readonly CancellationTokenSource cancel = new();
    readonly TextBox account = new(), password = new() { UseSystemPasswordChar = true }, guard = new() { Enabled = false };
    readonly Label message = new() { Text = "密码仅用于本次登录；登录成功后保存 Windows 加密令牌。", Dock = DockStyle.Fill };
    readonly Button submit = new() { Text = "登录", AutoSize = true };
    TaskCompletionSource<string>? requestedCode;
    SteamSession? session;

    SteamLoginForm(string dataRoot)
    {
        Text = "Steam 登录 · ColdClientManager";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(460, 290); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 92)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) layout.RowStyles.Add(new(SizeType.Absolute, 38));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 38));
        void Field(int row, string text, TextBox input)
        {
            layout.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            input.Dock = DockStyle.Fill; layout.Controls.Add(input, 1, row);
        }
        Field(0, "Steam 账号", account); Field(1, "密码", password); Field(2, "Steam Guard", guard);
        layout.Controls.Add(message, 0, 3); layout.SetColumnSpan(message, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(close); buttons.Controls.Add(submit);
        layout.Controls.Add(buttons, 0, 4); layout.SetColumnSpan(buttons, 2); Controls.Add(layout);
        CancelButton = close; AcceptButton = submit;
        async Task LoginAsync(bool saved)
        {
            account.Enabled = password.Enabled = submit.Enabled = false;
            message.Text = saved ? "正在使用保存的登录连接 Steam…" : "正在登录 Steam…";
            var authentication = saved ? null : new AuthSessionDetails { Username = account.Text.Trim(), Password = password.Text, Authenticator = this };
            password.Clear();
            try
            {
                session = await SteamSession.ConnectAsync(dataRoot, authentication, cancel.Token);
                if (cancel.IsCancellationRequested) { await session.DisposeAsync(); session = null; return; }
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException) { if (!IsDisposed) DialogResult = DialogResult.Cancel; }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                requestedCode = null; guard.Enabled = false; guard.Clear(); submit.Text = "登录";
                message.Text = ex.Message; account.Enabled = password.Enabled = submit.Enabled = !saved;
            }
        }
        submit.Click += async (_, _) => {
            if (requestedCode != null)
            {
                if (string.IsNullOrWhiteSpace(guard.Text)) return;
                var pending = requestedCode; requestedCode = null;
                guard.Enabled = submit.Enabled = false; pending.SetResult(guard.Text.Trim()); guard.Clear();
                message.Text = "正在验证 Steam Guard…"; return;
            }
            if (string.IsNullOrWhiteSpace(account.Text) || password.Text.Length == 0) { message.Text = "请输入 Steam 账号和密码。"; return; }
            await LoginAsync(false);
        };
        Shown += async (_, _) => { if (File.Exists(SteamSession.LoginPath(dataRoot))) await LoginAsync(true); };
        FormClosing += (_, _) => cancel.Cancel();
    }

    async Task<string> RequestCodeAsync(string text)
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await InvokeAsync(() => {
            requestedCode = pending; message.Text = text; guard.Enabled = submit.Enabled = true;
            submit.Text = "提交验证码"; guard.Focus();
        }, cancel.Token);
        return await pending.Task.WaitAsync(cancel.Token).ConfigureAwait(false);
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => RequestCodeAsync(
        (previousCodeWasIncorrect ? "验证码错误，请重新输入。" : "") + "请输入手机 Steam 令牌中的动态码。");

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => RequestCodeAsync(
        (previousCodeWasIncorrect ? "验证码错误，请重新输入。" : "") + $"请输入发送到 {email} 的 Steam Guard 验证码。");

    public async Task<bool> AcceptDeviceConfirmationAsync()
    {
        await InvokeAsync(() => message.Text = "请在手机 Steam 应用中确认本次账号密码登录。", cancel.Token);
        return true;
    }

    public static SteamSession SignIn(IWin32Window? owner, string dataRoot)
    {
        using var form = new SteamLoginForm(dataRoot);
        if (form.ShowDialog(owner) != DialogResult.OK) throw new OperationCanceledException("Steam 登录已取消。");
        return form.session!;
    }
}
