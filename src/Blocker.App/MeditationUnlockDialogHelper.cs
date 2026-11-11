using System;
using System.Windows;
using Blocker.Core;

namespace Blocker.App;

/// <summary>
/// Shared helper for showing meditation unlock dialogs (Password / TypingChallenge),
/// eliminating duplicate unlock dialog flows, running modal verification once per attempt,
/// failing closed on any cancellation/error, and pausing non-essential tracker work while open.
/// </summary>
internal static class MeditationUnlockDialogHelper
{
    internal const string DefaultMeditationChallenge =
        "I choose to return to my desktop with intention and focus.";

    internal static bool RequestUnlock(
        LockMode lockMode,
        Window? owner,
        Func<string, bool> verifyPassword,
        Func<string>? getChallengeText = null,
        ZenShieldWindow? activeShield = null,
        string promptTargetName = "Meditation Session",
        string? challengeTitle = null)
    {
        if (lockMode == LockMode.Password)
        {
            if (verifyPassword == null)
            {
                return false;
            }

            activeShield?.SetModalUnlockDialogOpen(true);
            try
            {
                var dlg = new PasswordUnlockDialog(promptTargetName)
                {
                    Owner = owner,
                    ValidatePassword = verifyPassword
                };

                bool? dialogResult = dlg.ShowDialog();
                // Decision comes strictly from authoritative verified flag AND DialogResult == true
                return dialogResult == true && dlg.IsVerified;
            }
            catch
            {
                return false;
            }
            finally
            {
                activeShield?.SetModalUnlockDialogOpen(false);
            }
        }

        if (lockMode == LockMode.TypingChallenge)
        {
            activeShield?.SetModalUnlockDialogOpen(true);
            try
            {
                string challenge = getChallengeText != null
                    ? getChallengeText()
                    : DefaultMeditationChallenge;

                var dlg = challengeTitle != null
                    ? new TypingChallengeDialog(challenge, challengeTitle)
                    : new TypingChallengeDialog(challenge);
                dlg.Owner = owner;

                return dlg.ShowDialog() == true;
            }
            catch
            {
                return false;
            }
            finally
            {
                activeShield?.SetModalUnlockDialogOpen(false);
            }
        }

        return lockMode == LockMode.None;
    }
}
