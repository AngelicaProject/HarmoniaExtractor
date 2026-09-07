namespace XivExdUnpacker.src.Services;

// Guards interactive console APIs: with redirected stdout (suite sync job,
// pipes) there is no console and these calls throw IOException.
internal static class ConsoleGuard
{
    public static ConsoleColor Color
    {
        set
        {
            try
            {
                if (!Console.IsOutputRedirected)
                    Console.ForegroundColor = value;
            }
            catch (IOException)
            {
            }
        }
    }

    public static void Reset()
    {
        try
        {
            if (!Console.IsOutputRedirected)
                Console.ResetColor();
        }
        catch (IOException)
        {
        }
    }

    public static void CursorVisible(bool visible)
    {
        try
        {
            if (!Console.IsOutputRedirected)
                Console.CursorVisible = visible;
        }
        catch (IOException)
        {
        }
    }

    public static int WindowWidthOr(int fallback)
    {
        try
        {
            return Console.IsOutputRedirected ? fallback : Console.WindowWidth;
        }
        catch (IOException)
        {
            return fallback;
        }
    }
}
