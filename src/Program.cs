using Raylib_cs;

namespace PixelLegion;

static class Program
{
    static void Main()
    {
        // if the game ever falls over, leave a note saying why (crash.txt next to the game)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.txt"), e.ExceptionObject.ToString()); }
            catch { /* nothing more we can do */ }
        };

        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint);
        Raylib.InitWindow(1280, 800, "Pixel Legion");
        Raylib.SetTargetFPS(60);
        Raylib.SetExitKey(KeyboardKey.Null); // Esc shouldn't instantly quit

        var game = new Game();
        bool soak = Environment.GetEnvironmentVariable("PIXEL_SOAK") != null; // fast-forward for crash hunting
        while (!Raylib.WindowShouldClose())
        {
            float dt = MathF.Min(Raylib.GetFrameTime(), 1f / 30f);
            if (soak) for (int i = 0; i < 9; i++) game.Update(1f / 60f);
            game.Update(dt);

            Raylib.BeginDrawing();
            game.Draw();
            Raylib.EndDrawing();
        }
        Raylib.CloseWindow();
    }
}
