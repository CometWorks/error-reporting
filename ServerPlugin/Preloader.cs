using System;

// Magnetar discovers this bare type before the dedicated server's Main.
// No game types are referenced here, so Initialize is safe before game assembly rewriting.
public static class Preloader
{
    public static void Initialize() => ServerPlugin.IncidentCapture.Start(AppContext.BaseDirectory);
    public static void Finish() => Initialize();
}
