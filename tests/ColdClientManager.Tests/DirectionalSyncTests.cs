using ColdClientManager;

internal static partial class Tests
{
    static void TestDirectionalSync(string root)
    {
        string local = Path.Combine(root, "directional", "local"), target = Path.Combine(root, "directional", "target"), state = Path.Combine(root, "directional", "state");
        var profile = new GameProfile { Rules = [new SyncRule { LocalPath = local, SharedPath = target, Patterns = "*.sav;*.dat" }] };
        string localSlot = Path.Combine(local, "slot.sav"), targetSlot = Path.Combine(target, "slot.sav");
        Write(localSlot, "unsynchronized-local"); Write(targetSlot, "target-before-play");
        Write(Path.Combine(local, "local-only.sav"), "local-extra");
        Write(Path.Combine(target, "nested", "new.dat"), "target-new-slot");
        Write(Path.Combine(target, "cache.bin"), "excluded");
        var pull = SyncEngine.Plan(profile, state, SyncDirection.TargetToLocal);
        Assert(pull.Items.All(i => i.Action == "Shared") && pull.Items.All(i => i.RelativePath != "local-only.sav"),
            "pre-play copy selects only target files and resolves differing content toward the local directory");
        string backup = SyncEngine.Apply(profile, state, pull);
        Assert(File.ReadAllText(localSlot) == "target-before-play" && File.ReadAllText(Path.Combine(local, "nested", "new.dat")) == "target-new-slot",
            "pre-play copy overwrites an existing local save and includes new nested target saves");
        Assert(File.ReadAllText(Path.Combine(local, "local-only.sav")) == "local-extra" && !File.Exists(Path.Combine(target, "local-only.sav"))
            && !File.Exists(Path.Combine(local, "cache.bin")), "pre-play copy preserves local-only files without uploading them or copying excluded extensions");
        Assert(Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == "unsynchronized-local"),
            "pre-play overwrite backs up the original local save");
        Write(localSlot, "played-local"); Write(targetSlot, "target-changed-during-play");
        Write(Path.Combine(target, "target-only.sav"), "target-extra");
        var manual = SyncEngine.Plan(profile, state);
        Assert(manual.Direction == SyncDirection.Both && manual.Items.Single(i => i.RelativePath == "slot.sav").Action == "Conflict",
            "manual synchronization retains its three-way conflict comparison");
        var push = SyncEngine.Plan(profile, state, SyncDirection.LocalToTarget);
        Assert(push.Items.Single(i => i.RelativePath == "slot.sav").Action == "Local" && push.Items.All(i => i.RelativePath != "target-only.sav"),
            "post-play copy chooses local progress even when the target also changed");
        backup = SyncEngine.Apply(profile, state, push);
        Assert(File.ReadAllText(targetSlot) == "played-local" && File.ReadAllText(Path.Combine(target, "local-only.sav")) == "local-extra",
            "post-play copy writes local progress and new local saves back to the target");
        Assert(File.ReadAllText(Path.Combine(target, "target-only.sav")) == "target-extra" && !File.Exists(Path.Combine(local, "target-only.sav")),
            "post-play copy keeps target-only files without pulling them back or deleting them");
        Assert(Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == "target-changed-during-play"),
            "post-play overwrite backs up the target's previous contents");
        Write(targetSlot, "planned-target");
        pull = SyncEngine.Plan(profile, state, SyncDirection.TargetToLocal);
        Write(targetSlot, "changed-after-preview");
        Throws<IOException>(() => SyncEngine.Apply(profile, state, pull), "directional copying rejects a source changed after preview");
        Assert(File.ReadAllText(localSlot) == "played-local", "a stale directional plan leaves the local save unchanged");
        Write(localSlot, "planned-local");
        push = SyncEngine.Plan(profile, state, SyncDirection.LocalToTarget);
        Write(targetSlot, "target-changed-after-preview");
        Throws<IOException>(() => SyncEngine.Apply(profile, state, push), "directional copying rejects a target changed after preview");
        Assert(File.ReadAllText(targetSlot) == "target-changed-after-preview", "a stale directional plan leaves the target save unchanged");
        string requestPath = Path.Combine(root, "directional", "elevated-request.json");
        Storage.Write(requestPath, new ElevatedSyncRequest(profile, state, null, SyncDirection.TargetToLocal));
        var request = Storage.Read<ElevatedSyncRequest>(requestPath);
        Assert(request.Direction == SyncDirection.TargetToLocal && SyncEngine.Plan(request.Profile, request.DataRoot, request.Direction)
            .Items.Single(i => i.RelativePath == "slot.sav").Action == "Shared", "elevated preview requests retain the pre-play pull direction");
        Storage.Write(requestPath, new ElevatedSyncRequest(profile, state, SyncEngine.Plan(profile, state, SyncDirection.LocalToTarget)));
        request = Storage.Read<ElevatedSyncRequest>(requestPath);
        SyncEngine.Apply(request.Profile, request.DataRoot, request.Plan!);
        Assert(request.Plan!.Direction == SyncDirection.LocalToTarget && File.ReadAllText(targetSlot) == "planned-local",
            "serialized elevated apply plans retain the post-play push direction");
        Throws<ArgumentOutOfRangeException>(() => SyncEngine.Apply(profile, state, new SyncPlan { Direction = (SyncDirection)99 }),
            "invalid persisted synchronization directions are rejected before copying");
        var single = new GameProfile { Rules = [new SyncRule { SingleFile = true, LocalPath = Path.Combine(root, "directional-single", "local.sav"),
            SharedPath = Path.Combine(root, "directional-single", "different-name.sav") }] };
        Write(single.Rules[0].LocalPath, "old-local"); Write(single.Rules[0].SharedPath, "single-target");
        SyncEngine.Apply(single, state, SyncEngine.Plan(single, state, SyncDirection.TargetToLocal));
        Assert(File.ReadAllText(single.Rules[0].LocalPath) == "single-target", "directional pull supports single-file rules with different names");
        Write(single.Rules[0].LocalPath, "single-played");
        SyncEngine.Apply(single, state, SyncEngine.Plan(single, state, SyncDirection.LocalToTarget));
        Assert(File.ReadAllText(single.Rules[0].SharedPath) == "single-played", "directional push supports single-file rules with different names");
    }
}
