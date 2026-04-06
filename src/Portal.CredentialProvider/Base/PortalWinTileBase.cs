using Lithnet.CredentialProvider;
using Portal.Common;
using Portal.CredentialProvider;
using Portal.CredentialProvider.Services;
using System.Threading.Tasks;

namespace Portal.CredentialProvider.Base;

public abstract class PortalWinTileBase : CredentialTile2
{
    protected readonly PortalWinProviderBase _providerBase;
    protected SmallLabelControl? _statusLabel;
    protected SmallLabelControl? _versionLabel;
    protected SmallLabelControl? _statusDetailsLabel;
    protected TextboxControl? _usernameControl;
    protected SecurePasswordTextboxControl? _passwordControl;
    protected CommandLinkControl? _showDetailsButton;
    protected CommandLinkControl? _hideDetailsButton;
    protected CommandLinkControl? _requestButton;
    protected CommandLinkControl? _cancelButton;
    private bool _isDetailsVisible;
    private string _lastStatusRaw = "Waiting for remote command.";

    protected PortalWinTileBase(PortalWinProviderBase provider) : base(provider)
    {
        _providerBase = provider;
    }

    protected PortalWinTileBase(PortalWinProviderBase provider, CredentialProviderUser user) : base(provider, user)
    {
        _providerBase = provider;
    }

    public override void Initialize()
    {
        try
        {
            _statusLabel = Controls.GetControl<SmallLabelControl>("StatusLabel");
            _versionLabel = Controls.GetControl<SmallLabelControl>("VersionLabel");
            _statusDetailsLabel = Controls.GetControl<SmallLabelControl>("StatusDetailsLabel");
            _usernameControl = Controls.GetControl<TextboxControl>("UsernameField");
            _passwordControl = Controls.GetControl<SecurePasswordTextboxControl>("PasswordField");
            _showDetailsButton = Controls.GetControl<CommandLinkControl>("ShowDetailsButton");
            _hideDetailsButton = Controls.GetControl<CommandLinkControl>("HideDetailsButton");
            _requestButton = Controls.GetControl<CommandLinkControl>("RequestButton");
            _cancelButton = Controls.GetControl<CommandLinkControl>("CancelButton");

            if (_versionLabel != null)
            {
                _versionLabel.Label = $"Ver: {GetProjectVersionText()}";
            }

            if (_showDetailsButton != null)
            {
                _showDetailsButton.OnClick = ShowDetails;
            }

            if (_hideDetailsButton != null)
            {
                _hideDetailsButton.OnClick = HideDetails;
            }

            ApplyDetailsVisibility();

            if (User != null && _usernameControl != null)
            {
                _usernameControl.Text = User.QualifiedUserName;
            }

            RefreshStatusFromProvider();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"[{GetType().Name}] Init error", ex);
        }
    }

    internal void UpdateStatus(string text)
    {
        _lastStatusRaw = string.IsNullOrWhiteSpace(text) ? "Waiting for remote command." : text.Trim();

        if (_providerBase is PortalWinProvider provider)
        {
            UpdateStatus(provider.BuildStatusHeadlineForState(_lastStatusRaw), provider.BuildStatusDetailsForState(_lastStatusRaw));
            return;
        }

        UpdateStatus(text, null);
    }

    internal void UpdateStatus(string headline, string? details)
    {
        if (_statusLabel != null)
        {
            try
            {
                _statusLabel.Label = string.IsNullOrWhiteSpace(headline) ? "PortalWin status unavailable" : headline.Trim();
            }
            catch
            {
            }
        }

        if (_statusDetailsLabel != null && details != null)
        {
            try
            {
                _statusDetailsLabel.Label = string.IsNullOrWhiteSpace(details) ? "Status unknown" : details.Trim();
            }
            catch
            {
            }
        }
    }

    internal void RefreshStatusFromProvider()
    {
        if (_providerBase is PortalWinProvider provider)
        {
            UpdateStatus(provider.BuildStatusHeadlineForState(_lastStatusRaw), provider.BuildStatusDetailsForState(_lastStatusRaw));
        }
    }

    protected void HideDetails()
    {
        _isDetailsVisible = false;
        ApplyDetailsVisibility();
    }

    protected void ShowDetails()
    {
        _isDetailsVisible = true;
        ApplyDetailsVisibility();
    }

    private void ApplyDetailsVisibility()
    {
        if (_statusDetailsLabel != null)
        {
            _statusDetailsLabel.State = _isDetailsVisible ? FieldState.DisplayInSelectedTile : FieldState.Hidden;
        }

        if (_showDetailsButton != null)
        {
            _showDetailsButton.State = _isDetailsVisible ? FieldState.Hidden : FieldState.DisplayInSelectedTile;
        }

        if (_hideDetailsButton != null)
        {
            _hideDetailsButton.State = _isDetailsVisible ? FieldState.DisplayInSelectedTile : FieldState.Hidden;
        }
    }

    private static string GetProjectVersionText()
    {
        var version = typeof(PortalWinTileBase).Assembly.GetName().Version;
        return version != null ? version.ToString(3) : "unknown";
    }

    protected override bool OnSelectedShouldAutoLogon() => _providerBase.UnlockState.ShouldAutoLogon;

    protected override void OnBeforeSerialize()
    {
        if (_providerBase.UnlockState.HasPendingUnlock)
        {
            _providerBase.ConsumePendingAutoLogon();
        }
    }

    protected override CredentialResponseBase GetCredentials()
    {
        var state = _providerBase.UnlockState;

        if (state.HasPendingUnlock)
        {
            string userToSubmit = !string.IsNullOrEmpty(state.Username)
                ? state.Username
                : (User != null ? User.UserName : "");
            string passToSubmit = state.GetPassword() ?? "";

            Logger.Log($"[{GetType().Name}] Submitting credentials. User: '{userToSubmit}', Domain: '{state.Domain ?? System.Environment.MachineName}'");
            CredentialProviderBootstrapper.TlsService?.DisconnectAllWebSocketClients("Unlock serialization (pending credentials)");
            var response = new CredentialResponseInsecure
            {
                IsSuccess = true,
                Username = userToSubmit,
                Domain = state.Domain ?? System.Environment.MachineName,
                Password = passToSubmit
            };

            // LogonUI may call GetSerialization multiple times.
            // Delay clear to ensure the credential survives validation.
            Task.Delay(3000).ContinueWith(_ => state.Clear());
            return response;
        }

        if (_passwordControl != null && _passwordControl.Password != null && _passwordControl.Password.Length > 0)
        {
            string localPassword = GetPlaintextPassword(_passwordControl.Password) ?? "";

            if (!string.IsNullOrEmpty(localPassword))
            {
                Logger.Log($"[{GetType().Name}] Submitting local password override.");
                CredentialProviderBootstrapper.TlsService?.DisconnectAllWebSocketClients("Unlock serialization (local password)");

                string fallbackDomain = System.Environment.MachineName;
                string fallbackUser = User?.UserName ?? _usernameControl?.Text ?? "";

                if (User != null && !string.IsNullOrEmpty(User.QualifiedUserName) && User.QualifiedUserName.Contains("\\"))
                {
                    var parts = User.QualifiedUserName.Split('\\');
                    if (parts.Length == 2)
                    {
                        fallbackDomain = parts[0];
                        fallbackUser = parts[1];
                    }
                }

                return new CredentialResponseInsecure
                {
                    IsSuccess = true,
                    Username = fallbackUser,
                    Domain = fallbackDomain,
                    Password = localPassword
                };
            }
        }

        return GetFallbackCredentials();
    }

    protected abstract CredentialResponseBase GetFallbackCredentials();

    protected string? GetPlaintextPassword(System.Security.SecureString? securePassword)
    {
        if (securePassword == null || securePassword.Length == 0)
        {
            return null;
        }

        System.IntPtr unmanagedString = System.IntPtr.Zero;
        try
        {
            unmanagedString = System.Runtime.InteropServices.Marshal.SecureStringToGlobalAllocUnicode(securePassword);
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(unmanagedString);
        }
        finally
        {
            if (unmanagedString != System.IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
            }
        }
    }
}
