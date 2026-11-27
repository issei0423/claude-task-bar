using System;
using System.Collections.Generic;

namespace ClaudeTaskBar;

/// <summary>One pet on the stage. X is the centre of its picture, in dots.</summary>
public sealed class PetActor
{
    internal PetActor(string sessionId, int slot, double x, SessionStatus status)
    {
        SessionId = sessionId;
        Slot = slot;
        X = x;
        Status = status;
    }

    public string SessionId { get; }
    public int Slot { get; }
    public double X { get; internal set; }
    /// <summary>
    /// "typing", "walk", "walkL", "done", "ask", "idle", "run", "runL",
    /// "spawn" or "vanish". The L pictures are used only when the sheet has them.
    /// </summary>
    public string Anim { get; internal set; } = "";
    /// <summary>Time in the current animation. Back to 0 whenever the animation changes.</summary>
    public double AnimMs { get; internal set; }
    /// <summary>Moving to the left (the direction, whatever picture is used).</summary>
    public bool FacingLeft { get; internal set; }
    /// <summary>Draw the picture mirrored: facing left with no left-facing picture in the sheet.</summary>
    public bool Flip { get; internal set; }
    public SessionStatus Status { get; internal set; }
    /// <summary>Has walked out to its front spot and stands there.</summary>
    public bool AtFront { get; internal set; }
    /// <summary>Running back and forth across the stage (the usage is high); also true while it startles first.</summary>
    public bool Fleeing { get; internal set; }
    /// <summary>Playing the startle (once, on the spot) that comes just before it starts to run.</summary>
    public bool Startling { get; internal set; }
    /// <summary>Its bubble "もう無理！" is due (it shows for a moment every now and then while running).</summary>
    public bool Muttering { get; internal set; }
    /// <summary>Playing its birth animation (once). It stays at its seat meanwhile.</summary>
    public bool Spawning { get; internal set; }
    /// <summary>
    /// Playing its leaving animation (once); the session is already gone. The
    /// seat stays taken, no call for attention is made, and a click does nothing.
    /// </summary>
    public bool Leaving { get; internal set; }

    internal int RunDir;
    internal double RunMs;
}

/// <summary>
/// Where the pets are and what they are doing. Pure logic (no WPF) so CoreTests
/// can check it; PetBarWindow only draws what this says.
/// Units are dots; x is 0 at the stage's left edge and grows to the right.
/// Each pet has a home seat (right side, seat 0 the rightmost). When it wants
/// attention (Asking, DoneUnseen) it walks out to its front spot to the left,
/// and walks back when the session goes back to work.
/// While the usage is high (SetFleeing) the pets that do not want attention run
/// back and forth over the whole stage instead.
/// </summary>
public sealed class PetStage
{
    public const int SlotPitch = 26;
    public const int FrontGap = 6;
    public const int EdgePad = 4;
    /// <summary>Dots per second.</summary>
    public const double WalkSpeed = 30;
    /// <summary>Dots per second while running away (the usage is high).</summary>
    public const double FleeSpeed = 90;
    public const int Slots = PetSelection.MaxDisplayed;

    public const int WidthDots = 2 * Slots * SlotPitch + FrontGap + 2 * EdgePad;

    /// <summary>What a running pet says now and then (shown as a bubble).</summary>
    public const string MutterText = "もう無理！";
    /// <summary>A running pet says it every this many milliseconds...</summary>
    public const double MutterPeriodMs = 12000;
    /// <summary>...for this long. The pets are staggered by seat so they do not all say it together.</summary>
    public const double MutterShowMs = 2500;

    /// <summary>Half a picture: a running pet's centre stays this far inside the stage edges.</summary>
    public const int FleeMargin = 16;
    public const double FleeMinX = FleeMargin;
    public const double FleeMaxX = WidthDots - FleeMargin;

    public static double HomeX(int slot) => WidthDots - EdgePad - SlotPitch / 2.0 - slot * SlotPitch;
    public static double FrontX(int slot) => HomeX(slot) - (Slots * SlotPitch + FrontGap);

    private readonly PetActor?[] _bySlot = new PetActor?[Slots];
    private IReadOnlyList<SessionInfo> _lastDisplayed = Array.Empty<SessionInfo>();

    // What the pet sheet can do. Without UseSheet nothing optional is available,
    // so the pets mirror when they go left, never flee, and appear/leave at once.
    private bool _hasWalkL, _hasRun, _hasRunL;
    private double _spawnMs, _vanishMs, _startleMs;

    /// <summary>The pets in seat order (pets that are leaving are included until they are done).</summary>
    public IReadOnlyList<PetActor> Actors { get; private set; } = Array.Empty<PetActor>();

    /// <summary>True while the pets are told to run away (and the sheet has a run picture).</summary>
    public bool Fleeing { get; private set; }

    /// <summary>
    /// Tell the stage which optional animations the pet sheet has (walkL, run,
    /// runL, spawn, vanish). Null = none of them.
    /// </summary>
    public void UseSheet(PetSheet? sheet)
    {
        _hasWalkL = sheet?.Find("walkL") != null;
        _hasRun = sheet?.Find("run") != null;
        _hasRunL = sheet?.Find("runL") != null;
        _spawnMs = sheet?.Find("spawn")?.TotalMs ?? 0;
        _vanishMs = sheet?.Find("vanish")?.TotalMs ?? 0;
        _startleMs = sheet?.Find("startle")?.TotalMs ?? 0;
    }

    /// <summary>
    /// Run away (true) or go back to the seats (false). The moment it changes
    /// from false to true, every pet that is about to run (not calling for
    /// attention, not being born or leaving) first plays the startle once on the
    /// spot, if the sheet has one. Pets that appear or get back to work later
    /// start running without it. Calling it again with the same value does nothing.
    /// </summary>
    public void SetFleeing(bool on)
    {
        bool starting = on && !Fleeing;
        Fleeing = on;
        if (!starting || !_hasRun || _startleMs <= 0) return;
        foreach (var a in Actors)
        {
            if (a.Leaving || a.Spawning || WantsFront(a.Status)) continue;
            a.Startling = true;
            Update(a, 0);
        }
    }

    private static double MutterOffset(int slot) => slot * (MutterPeriodMs / Slots);

    private static void StopRunning(PetActor a)
    {
        a.Fleeing = false;
        a.Startling = false;
        a.Muttering = false;
        a.RunDir = 0;
        a.RunMs = 0;
    }

    /// <summary>
    /// Match the stage to the sessions being shown. Pets already here keep their
    /// seat and only get the new status; a new session takes the lowest free seat
    /// (and plays the birth animation when announceNew is true and the sheet has
    /// one); a pet whose session is gone plays the leaving animation and is taken
    /// off when it is done (at once when the sheet has none); a leaving pet whose
    /// session came back stays.
    /// </summary>
    public void Sync(IReadOnlyList<SessionInfo> displayed, bool announceNew = true)
    {
        _lastDisplayed = displayed.Count == 0 ? Array.Empty<SessionInfo>() : new List<SessionInfo>(displayed);

        var ids = new HashSet<string>();
        foreach (var s in displayed) ids.Add(s.Id);

        for (int i = 0; i < _bySlot.Length; i++)
        {
            var a = _bySlot[i];
            if (a == null || ids.Contains(a.SessionId) || a.Leaving) continue;
            if (_vanishMs > 0)
            {
                a.Leaving = true;
                a.Spawning = false;
                Update(a, 0);
            }
            else _bySlot[i] = null;
        }

        foreach (var s in displayed)
        {
            PetActor? found = null;
            foreach (var a in _bySlot)
                if (a != null && a.SessionId == s.Id) { found = a; break; }
            if (found != null)
            {
                found.Status = s.Status;
                if (found.Leaving) { found.Leaving = false; Update(found, 0); } // it came back: stay
                continue;
            }

            int free = Array.IndexOf(_bySlot, null);
            if (free < 0) continue; // no seat left (the caller caps at MaxDisplayed)
            var actor = new PetActor(s.Id, free, HomeX(free), s.Status);
            actor.Spawning = announceNew && _spawnMs > 0;
            Update(actor, 0);
            _bySlot[free] = actor;
        }

        RebuildActors();
    }

    private void RebuildActors()
    {
        var list = new List<PetActor>();
        foreach (var a in _bySlot) if (a != null) list.Add(a);
        Actors = list;
    }

    /// <summary>Move time forward. Negative, NaN and infinite steps count as 0.</summary>
    public void Advance(double dtMs)
    {
        if (double.IsNaN(dtMs) || double.IsInfinity(dtMs) || dtMs < 0) dtMs = 0;
        foreach (var a in Actors) Update(a, dtMs);

        // Pets that finished leaving give their seat back.
        bool freed = false;
        for (int i = 0; i < _bySlot.Length; i++)
        {
            var a = _bySlot[i];
            if (a != null && a.Leaving && a.AnimMs >= _vanishMs) { _bySlot[i] = null; freed = true; }
        }
        // A session that had no seat while the leaving pets blocked them takes one now.
        if (freed) Sync(_lastDisplayed, true);
    }

    private static bool WantsFront(SessionStatus st) =>
        st == SessionStatus.Asking || st == SessionStatus.DoneUnseen;

    private static void SetAnim(PetActor a, string anim, double dtMs)
    {
        if (anim != a.Anim) { a.Anim = anim; a.AnimMs = 0; }
        else a.AnimMs += dtMs;
    }

    private void Update(PetActor a, double dtMs)
    {
        if (a.Leaving)
        {
            a.AtFront = false;
            StopRunning(a);
            a.Flip = false;
            SetAnim(a, "vanish", dtMs);
            return;
        }

        if (a.Spawning)
        {
            a.AtFront = false;
            StopRunning(a);
            a.FacingLeft = false;
            a.Flip = false;
            SetAnim(a, "spawn", dtMs);
            if (a.AnimMs < _spawnMs) return;
            a.Spawning = false; // born: go on as usual from here
            dtMs = 0;
        }

        bool front = WantsFront(a.Status);
        if (Fleeing && _hasRun && !front)
        {
            if (a.Startling)
            {
                a.Fleeing = true;
                a.Muttering = false;
                a.AtFront = false;
                a.FacingLeft = false;
                a.Flip = false;
                SetAnim(a, "startle", dtMs);
                if (a.AnimMs < _startleMs) return;
                a.Startling = false; // startled once: now run
                dtMs = 0;
            }
            Run(a, dtMs);
            return;
        }
        StopRunning(a);

        double target = front ? FrontX(a.Slot) : HomeX(a.Slot);
        double gap = target - a.X;
        double step = WalkSpeed * dtMs / 1000.0;

        string anim;
        if (Math.Abs(gap) <= step + 1e-9)
        {
            a.X = target; // arrived (never overshoots)
            a.AtFront = front;
            a.FacingLeft = false;
            a.Flip = false;
            anim = a.Status switch
            {
                SessionStatus.Running => "typing",
                SessionStatus.Asking => "ask",
                SessionStatus.DoneUnseen => "done",
                _ => "idle",
            };
        }
        else
        {
            a.X += gap < 0 ? -step : step;
            a.AtFront = false;
            bool left = gap < 0;
            a.FacingLeft = left;
            a.Flip = left && !_hasWalkL;
            anim = left && _hasWalkL ? "walkL" : "walk";
        }

        SetAnim(a, anim, dtMs);
    }

    /// <summary>
    /// One step of running back and forth between FleeMinX and FleeMaxX. A pet
    /// that starts running heads left first (away from the seats), or right if
    /// it is already at the left end.
    /// </summary>
    private void Run(PetActor a, double dtMs)
    {
        a.Fleeing = true;
        if (a.RunDir == 0) a.RunDir = a.X > FleeMinX + 1e-9 ? -1 : 1;

        const double len = FleeMaxX - FleeMinX;
        double x = Math.Min(Math.Max(a.X, FleeMinX), FleeMaxX);
        // u runs 0..len going right and len..2*len coming back, then wraps.
        double u = a.RunDir > 0 ? x - FleeMinX : len + (FleeMaxX - x);
        u = (u + FleeSpeed * dtMs / 1000.0) % (2 * len);
        if (u < len) { a.RunDir = 1; a.X = FleeMinX + u; }
        else { a.RunDir = -1; a.X = FleeMaxX - (u - len); }

        a.RunMs += dtMs;
        a.Muttering = (a.RunMs + MutterOffset(a.Slot)) % MutterPeriodMs >= MutterPeriodMs - MutterShowMs;

        a.AtFront = false;
        bool left = a.RunDir < 0;
        a.FacingLeft = left;
        a.Flip = left && !_hasRunL;
        SetAnim(a, left && _hasRunL ? "runL" : "run", dtMs);
    }
}
