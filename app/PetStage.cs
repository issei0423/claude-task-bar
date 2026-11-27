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
    /// <summary>"typing", "walk", "done", "ask" or "idle".</summary>
    public string Anim { get; internal set; } = "";
    /// <summary>Time in the current animation. Back to 0 whenever the animation changes.</summary>
    public double AnimMs { get; internal set; }
    public bool FacingLeft { get; internal set; }
    public SessionStatus Status { get; internal set; }
    /// <summary>Has walked out to its front spot and stands there.</summary>
    public bool AtFront { get; internal set; }
}

/// <summary>
/// Where the pets are and what they are doing. Pure logic (no WPF) so CoreTests
/// can check it; PetBarWindow only draws what this says.
/// Units are dots; x is 0 at the stage's left edge and grows to the right.
/// Each pet has a home seat (right side, seat 0 the rightmost). When it wants
/// attention (Asking, DoneUnseen) it walks out to its front spot to the left,
/// and walks back when the session goes back to work.
/// </summary>
public sealed class PetStage
{
    public const int SlotPitch = 26;
    public const int FrontGap = 6;
    public const int EdgePad = 4;
    /// <summary>Dots per second.</summary>
    public const double WalkSpeed = 30;
    public const int Slots = PetSelection.MaxDisplayed;

    public const int WidthDots = 2 * Slots * SlotPitch + FrontGap + 2 * EdgePad;

    public static double HomeX(int slot) => WidthDots - EdgePad - SlotPitch / 2.0 - slot * SlotPitch;
    public static double FrontX(int slot) => HomeX(slot) - (Slots * SlotPitch + FrontGap);

    private readonly PetActor?[] _bySlot = new PetActor?[Slots];

    /// <summary>The pets in seat order.</summary>
    public IReadOnlyList<PetActor> Actors { get; private set; } = Array.Empty<PetActor>();

    /// <summary>
    /// Match the stage to the sessions being shown. Pets already here keep their
    /// seat and only get the new status; pets whose session is gone leave; a
    /// new session takes the lowest free seat and appears at its home spot.
    /// </summary>
    public void Sync(IReadOnlyList<SessionInfo> displayed)
    {
        var ids = new HashSet<string>();
        foreach (var s in displayed) ids.Add(s.Id);

        for (int i = 0; i < _bySlot.Length; i++)
        {
            var a = _bySlot[i];
            if (a != null && !ids.Contains(a.SessionId)) _bySlot[i] = null;
        }

        foreach (var s in displayed)
        {
            PetActor? found = null;
            foreach (var a in _bySlot)
                if (a != null && a.SessionId == s.Id) { found = a; break; }
            if (found != null) { found.Status = s.Status; continue; }

            int free = Array.IndexOf(_bySlot, null);
            if (free < 0) continue; // no seat left (the caller caps at MaxDisplayed)
            var actor = new PetActor(s.Id, free, HomeX(free), s.Status);
            Update(actor, 0);
            _bySlot[free] = actor;
        }

        var list = new List<PetActor>();
        foreach (var a in _bySlot) if (a != null) list.Add(a);
        Actors = list;
    }

    /// <summary>Move time forward. Negative, NaN and infinite steps count as 0.</summary>
    public void Advance(double dtMs)
    {
        if (double.IsNaN(dtMs) || double.IsInfinity(dtMs) || dtMs < 0) dtMs = 0;
        foreach (var a in Actors) Update(a, dtMs);
    }

    private static bool WantsFront(SessionStatus st) =>
        st == SessionStatus.Asking || st == SessionStatus.DoneUnseen;

    private static void Update(PetActor a, double dtMs)
    {
        bool front = WantsFront(a.Status);
        double target = front ? FrontX(a.Slot) : HomeX(a.Slot);
        double gap = target - a.X;
        double step = WalkSpeed * dtMs / 1000.0;

        string anim;
        if (Math.Abs(gap) <= step + 1e-9)
        {
            a.X = target; // arrived (never overshoots)
            a.AtFront = front;
            a.FacingLeft = false;
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
            a.FacingLeft = gap < 0;
            anim = "walk";
        }

        if (anim != a.Anim) { a.Anim = anim; a.AnimMs = 0; }
        else a.AnimMs += dtMs;
    }
}
