using ColdClientManager;

internal static partial class Tests
{
    static void TestLaunchModes(string root, string mock)
    {
        Application.EnableVisualStyles();
        foreach (string action in new[] { "同步并启动", "仅同步", "仅启动" })
        {
            string state = Path.Combine(root, action), fixture = Path.Combine(state, "fixture"), shared = Path.Combine(state, "shared");
            string localSave = Path.Combine(fixture, "profile", "saves", "slot1.sav"), sharedSave = Path.Combine(shared, "slot1.sav");
            Write(sharedSave, "remote-progress");
            var profile = new GameProfile { Name = "launch mode fixture", Executable = mock, WorkingDirectory = Path.GetDirectoryName(mock)!,
                Arguments = "launch-mode \"" + fixture + "\"", Rules = [new SyncRule { LocalPath = Path.GetDirectoryName(localSave)!, SharedPath = shared, Patterns = "*.sav;*.dat" }] };
            Storage.Write(Path.Combine(state, "profiles.json"), new[] { profile });
            using var form = new MainForm(state) { ShowInTaskbar = false, Opacity = 0 };
            Button? selectedAction = null;
            bool clicked = false;
            bool disabledDuringRun = false;
            IEnumerable<Control> Descendants(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    yield return child;
                    foreach (var nested in Descendants(child)) yield return nested;
                }
            }
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) => {
                if (clicked && selectedAction!.Enabled) { timer.Stop(); form.Close(); }
            };
            form.Shown += (_, _) => {
                selectedAction = Descendants(form).OfType<Button>().Single(b => b.Text == action);
                timer.Start(); selectedAction.PerformClick(); clicked = true;
                disabledDuringRun = Descendants(form).OfType<ListBox>().All(list => !list.Enabled)
                    && Descendants(form).OfType<Button>().Where(b => new[] { "同步并启动", "仅同步", "仅启动", "编辑设置", "添加应用" }.Contains(b.Text)).All(b => !b.Enabled);
            };
            Application.Run(form);
            if (action != "仅同步") Assert(disabledDuringRun, action + " prevents changing or editing the selected application while running");
            string observed = Path.Combine(fixture, "launch-input.txt");
            if (action == "同步并启动")
            {
                Assert(File.ReadAllText(observed) == "remote-progress", "sync-and-launch pulls the saved configuration's files before launching");
                Assert(File.ReadAllText(sharedSave) == "grandchild-final" && File.Exists(Path.Combine(shared, "sub", "slot3.dat")),
                    "sync-and-launch pushes progress after the entire game process tree exits");
            }
            else if (action == "仅同步")
            {
                Assert(File.ReadAllText(localSave) == "remote-progress" && !File.Exists(observed), "sync-only copies saves without launching the executable");
            }
            else
            {
                Assert(File.ReadAllText(observed) == "missing", "launch-only does not pull saves before launch");
                Assert(File.ReadAllText(localSave) == "grandchild-final" && File.ReadAllText(sharedSave) == "remote-progress",
                    "launch-only waits for descendants without uploading their progress afterward");
                Assert(!Directory.Exists(Storage.ProfileFolder(state, profile)), "launch-only does not create save synchronization baselines or backups");
            }
        }
    }
}
