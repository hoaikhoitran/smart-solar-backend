using System;

namespace SmartSolar.Modules.Identity.Enums;

public enum AuthActionTokenType
{
    EmailVerification = 1,
    PasswordReset = 2,

    /// <summary>Admin-provisioned account: proves the email and sets the first password.</summary>
    AccountSetup = 3
}
