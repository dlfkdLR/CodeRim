namespace CodeRim.Windows.Services;
internal static class SessionChime
{
    internal static readonly string[] Names = ["Asterisk", "Exclamation", "Beep", "Hand", "Question"];
    internal static void Play(string name)
    {
        var sound = name switch
        {
            "Exclamation" => System.Media.SystemSounds.Exclamation,
            "Beep" => System.Media.SystemSounds.Beep,
            "Hand" => System.Media.SystemSounds.Hand,
            "Question" => System.Media.SystemSounds.Question,
            _ => System.Media.SystemSounds.Asterisk
        };
        sound.Play();
    }
}
