using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

public sealed class LocalizationServiceTests
{
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
