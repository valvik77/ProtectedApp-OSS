namespace ProtectedApp.Services;

/// <summary>
/// The typed confirmation for permanently deleting a vault. The dialog takes both
/// its prompt and its validation from this one type, so what the user is asked to
/// type and what is accepted cannot drift apart.
/// </summary>
/// <remarks>
/// The prompt is localized here, when the dialog is built, rather than left for
/// the control-translation pass to reach later: a prompt that stayed in Spanish
/// while only the English word was accepted would block the user. The original
/// Spanish word is always accepted as well, so a prompt in either language can
/// never be answered with a word that is rejected.
/// </remarks>
internal static class PermanentDeletionConfirmation
{
    internal const string SourceWord = "ELIMINAR";
    internal const string SourcePrompt = "Escribe ELIMINAR para confirmar";

    public static string Prompt => LocalizationService.T(SourcePrompt);

    public static string LocalizedWord => LocalizationService.T(SourceWord);

    public static int MaxLength => Math.Max(SourceWord.Length, LocalizedWord.Length);

    public static bool IsConfirmed(string? text) =>
        text is not null
        && (text.Equals(SourceWord, StringComparison.Ordinal)
            || text.Equals(LocalizedWord, StringComparison.Ordinal));
}
