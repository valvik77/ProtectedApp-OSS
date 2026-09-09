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
}
