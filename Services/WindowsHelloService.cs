using System;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace ProtectedApp.Services;

public static class WindowsHelloService
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> GetStatusDescriptionAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability switch
            {
                UserConsentVerifierAvailability.Available => "Disponible y configurado en este equipo",
                UserConsentVerifierAvailability.DeviceNotPresent => "No hay sensor biométrico o PIN configurado en este equipo",
                UserConsentVerifierAvailability.NotConfiguredForUser => "No se ha configurado un PIN o biometría para tu usuario de Windows",
                UserConsentVerifierAvailability.DisabledByPolicy => "Deshabilitado por directivas del sistema",
                UserConsentVerifierAvailability.DeviceBusy => "El dispositivo biométrico está ocupado",
                _ => "No disponible"
            };
        }
        catch (Exception ex)
        {
            return $"Error al verificar disponibilidad: {ex.Message}";
        }
    }

    public static async Task<bool> VerifyUserConsentAsync(string prompt = "Autoriza el acceso a ProtectedApp")
    {
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(prompt);
            return result == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }
}
