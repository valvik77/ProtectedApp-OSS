using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void Localizes_TheRecycleBinMultipleSelection_InBothDirections()
    {
        try
        {
            LocalizationService.SetLanguage("en");

            Assert.Equal("Use Shift to select a range and Ctrl to pick individual files.",
                LocalizationService.T("Usa Mayús para seleccionar un rango y Ctrl para elegir archivos sueltos."));
            Assert.Equal("Some files could not be restored",
                LocalizationService.T("No se pudieron restaurar todos los archivos"));
            // The counted variant must win over the generic "Eliminar (.+)" one.
            Assert.Equal("Delete 4 files from the recycle bin",
                LocalizationService.T("Eliminar 4 archivos de la papelera"));
            Assert.Equal("Delete notas.txt from the recycle bin",
                LocalizationService.T("Eliminar notas.txt de la papelera"));
            Assert.Equal("2 of 5 were deleted.", LocalizationService.T("Se eliminaron 2 de 5."));

            LocalizationService.SetLanguage("es");

            Assert.Equal("Eliminar 4 archivos de la papelera",
                LocalizationService.T("Delete 4 files from the recycle bin"));
            Assert.Equal("Eliminar notas.txt de la papelera",
                LocalizationService.T("Delete notas.txt from the recycle bin"));
            Assert.Equal("Se eliminaron 2 de 5.", LocalizationService.T("2 of 5 were deleted."));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void Localizes_TheVaultRecycleBinDialog_InBothDirections()
    {
        try
        {
            LocalizationService.SetLanguage("en");

            Assert.Equal("Vault recycle bin", LocalizationService.T("Papelera de la bóveda"));
            Assert.Equal("Restore", LocalizationService.T("Restaurar"));
            Assert.Equal("Delete permanently", LocalizationService.T("Eliminar definitivamente"));
            Assert.Equal("Recycle bin of Documentos", LocalizationService.T("Papelera de Documentos"));
            Assert.Equal(
                "3 deleted file(s) are retained inside the vault. The space is reclaimed when you save and lock.",
                LocalizationService.T("3 archivo(s) eliminados se conservan dentro de la bóveda. El espacio se libera al guardar y bloquear."));
            Assert.Equal("File restored from the recycle bin: sub/deep.txt",
                LocalizationService.T("Archivo restaurado desde la papelera: sub/deep.txt"));

            LocalizationService.SetLanguage("es");

            Assert.Equal("Papelera de la bóveda", LocalizationService.T("Vault recycle bin"));
            Assert.Equal("Papelera de Documentos", LocalizationService.T("Recycle bin of Documentos"));
            Assert.Equal("Archivo restaurado desde la papelera: sub/deep.txt",
                LocalizationService.T("File restored from the recycle bin: sub/deep.txt"));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void Localizes_TheVaultRecycleBinCommand_InBothDirections()
    {
        try
        {
            LocalizationService.SetLanguage("en");

            Assert.Equal("Empty the vault's recycle bin",
                LocalizationService.T("Vaciar papelera de la bóveda"));
            Assert.Equal("Recycle bin is empty", LocalizationService.T("Papelera vacía"));
            Assert.Equal("Deleted files that can still be recovered",
                LocalizationService.T("Archivos eliminados que todavía pueden recuperarse"));
            Assert.Equal("Recycle bin emptied: 3 file(s) discarded",
                LocalizationService.T("Papelera vaciada: 3 archivo(s) descartados"));
            Assert.Equal(
                "2 deleted file(s) that could still be recovered will be discarded permanently. The space is reclaimed when the vault is saved and locked.",
                LocalizationService.T("Se descartarán definitivamente 2 archivo(s) eliminados que todavía podían recuperarse. El espacio se libera al guardar y bloquear la bóveda."));

            LocalizationService.SetLanguage("es");

            Assert.Equal("Vaciar papelera de la bóveda",
                LocalizationService.T("Empty the vault's recycle bin"));
            Assert.Equal("Papelera vaciada: 3 archivo(s) descartados",
                LocalizationService.T("Recycle bin emptied: 3 file(s) discarded"));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void Localizes_TheUnresponsiveCloseAndDailyQuotaSettings_InBothDirections()
    {
        try
        {
            LocalizationService.SetLanguage("en");

            Assert.Equal("Force the application closed if it does not respond",
                LocalizationService.T("Forzar el cierre si la aplicación no responde"));
            Assert.Equal("Maximum time per day", LocalizationService.T("Tiempo diario máximo"));
            Assert.Equal("No daily limit", LocalizationService.T("Sin límite diario"));
            // The longer runtime message must not be truncated by the shorter
            // pattern it starts with.
            Assert.Equal("Notepad has used up its daily allowance of 120 min. Use the master password to override it.",
                LocalizationService.T("Notepad ha agotado su tiempo diario de 120 min. Usa la contraseña maestra para anularlo."));
            Assert.Equal("Notepad has used up its daily allowance of 120 min.",
                LocalizationService.T("Notepad ha agotado su tiempo diario de 120 min."));

            LocalizationService.SetLanguage("es");

            Assert.Equal("Forzar el cierre si la aplicación no responde",
                LocalizationService.T("Force the application closed if it does not respond"));
            Assert.Equal("Tiempo diario máximo", LocalizationService.T("Maximum time per day"));
            Assert.Equal("Notepad ha agotado su tiempo diario de 120 min. Usa la contraseña maestra para anularlo.",
                LocalizationService.T("Notepad has used up its daily allowance of 120 min. Use the master password to override it."));
            Assert.Equal("Notepad ha agotado su tiempo diario de 120 min.",
                LocalizationService.T("Notepad has used up its daily allowance of 120 min."));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void Localizes_TheCloseWarningExtensionSelector_InBothDirections()
    {
        try
        {
            LocalizationService.SetLanguage("en");

            Assert.Equal("Extend", LocalizationService.T("Ampliar"));
            Assert.Equal("Extend this time", LocalizationService.T("Ampliar esta vez"));
            Assert.Equal("Extension minutes", LocalizationService.T("Minutos de ampliación"));
            Assert.Equal("Do not extend", LocalizationService.T("No ampliar"));
            // The durations offered by the selector.
            Assert.Equal("1 hour", LocalizationService.T("1 hora"));
            Assert.Equal("2 hours", LocalizationService.T("2 horas"));

            LocalizationService.SetLanguage("es");

            // The reverse lookup only works for one-to-one translations, so a
            // later duplicate English value would silently break this.
            Assert.Equal("Ampliar", LocalizationService.T("Extend"));
            Assert.Equal("Ampliar esta vez", LocalizationService.T("Extend this time"));
            Assert.Equal("Minutos de ampliación", LocalizationService.T("Extension minutes"));
            Assert.Equal("2 horas", LocalizationService.T("2 hours"));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void Localizes_NewlyAuditedControlsAndRuntimeMessages_InBothDirections()
    {
        try
        {
            LocalizationService.SetLanguage("en");

            Assert.Equal("Lock selected group", LocalizationService.T("Bloquear grupo seleccionado"));
            Assert.Equal("No shortcut configured.", LocalizationService.T("Sin atajo configurado."));
            Assert.Equal("Global shortcut: Ctrl+Alt+B", LocalizationService.T("Atajo global: Ctrl+Alt+B"));
            Assert.Equal(
                "Could not prepare the original folder for deletion. No files were deleted. Close applications using it and check its permissions:\r\nC:\\Data",
                LocalizationService.T("No se pudo preparar la carpeta original para eliminarla. No se eliminó ningún archivo. Cierra las aplicaciones que la usen y revisa sus permisos:\r\nC:\\Data"));

            LocalizationService.SetLanguage("es");

            Assert.Equal("Bloquear grupo seleccionado", LocalizationService.T("Lock selected group"));
            Assert.Equal("Atajo global: Ctrl+Alt+B", LocalizationService.T("Global shortcut: Ctrl+Alt+B"));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void Localizes_DialogMessagesThatPreviouslyFellBackToSpanish()
    {
        try
        {
            LocalizationService.SetLanguage("en");

            Assert.Equal("That file is already in your list.", LocalizationService.T("Ese archivo ya aparece en tu lista."));
            Assert.Equal("That container is already in the list.", LocalizationService.T("Ese contenedor ya aparece en la lista."));
            Assert.Equal("Cannot delete", LocalizationService.T("No se puede eliminar"));
            Assert.Equal("Uninstaller unavailable", LocalizationService.T("Desinstalador no disponible"));
            Assert.Equal("Unlock time (minutes)", LocalizationService.T("Tiempo de desbloqueo (minutos)"));
            Assert.Equal("Keep existing encrypted backups (.bak, TPM, and scheduled)",
                LocalizationService.T("Conservar las copias cifradas existentes (.bak, TPM y programadas)"));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void VaultPasswordLengthMessages_StateTheEnforcedMinimum()
    {
        try
        {
            LocalizationService.SetLanguage("en");

            // The vault edit dialog rejects passwords shorter than PasswordService.MinimumPasswordLength.
            Assert.Equal(12, PasswordService.MinimumPasswordLength);
            Assert.Equal("The new password must be at least 12 characters.",
                LocalizationService.T("La nueva contraseña debe tener al menos 12 caracteres."));
            Assert.Equal("The password must be at least 8 characters.",
                LocalizationService.T("La contraseña debe tener al menos 8 caracteres."));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void PermanentDeletionConfirmation_PromptNamesTheWordThatIsAccepted()
    {
        try
        {
            foreach (var (language, expectedWord) in new[] { ("en", "DELETE"), ("es", "ELIMINAR") })
            {
                LocalizationService.SetLanguage(language);

                Assert.Equal(expectedWord, PermanentDeletionConfirmation.LocalizedWord);
                // What the dialog shows is exactly what it accepts.
                Assert.Contains(PermanentDeletionConfirmation.LocalizedWord, PermanentDeletionConfirmation.Prompt,
                    StringComparison.Ordinal);
                Assert.True(PermanentDeletionConfirmation.IsConfirmed(PermanentDeletionConfirmation.LocalizedWord));
                Assert.True(PermanentDeletionConfirmation.MaxLength >= PermanentDeletionConfirmation.LocalizedWord.Length);
            }

            LocalizationService.SetLanguage("en");
            Assert.Equal("Type DELETE to confirm", PermanentDeletionConfirmation.Prompt);
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void PermanentDeletionConfirmation_NeverBlocksAPromptedWordAndStaysStrict()
    {
        try
        {
            foreach (var language in new[] { "en", "es" })
            {
                LocalizationService.SetLanguage(language);

                // The original word is always accepted, so a prompt that was not
                // translated (or was translated late) can never lock the user out.
                Assert.True(PermanentDeletionConfirmation.IsConfirmed("ELIMINAR"));
                Assert.True(PermanentDeletionConfirmation.IsConfirmed("DELETE") == (language == "en"));

                Assert.False(PermanentDeletionConfirmation.IsConfirmed(null));
                Assert.False(PermanentDeletionConfirmation.IsConfirmed(string.Empty));
                Assert.False(PermanentDeletionConfirmation.IsConfirmed("eliminar"));
                Assert.False(PermanentDeletionConfirmation.IsConfirmed("ELIMINAR "));
                Assert.False(PermanentDeletionConfirmation.IsConfirmed("ELIMINA"));
            }
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }

    [Fact]
    public void PermanentDeletionConfirmation_PromptSurvivesRetranslationAndLanguageSwitch()
    {
        try
        {
            // ApplyTo runs again when the dialog loads and when the language changes;
            // it feeds the already-rendered prompt back through T().
            LocalizationService.SetLanguage("en");
            var english = PermanentDeletionConfirmation.Prompt;
            Assert.Equal(english, LocalizationService.T(english));

            LocalizationService.SetLanguage("es");
            var spanish = LocalizationService.T(english);
            Assert.Equal(PermanentDeletionConfirmation.SourcePrompt, spanish);
            Assert.Equal(spanish, LocalizationService.T(spanish));
        }
        finally
        {
            LocalizationService.SetLanguage("es");
        }
    }
}
