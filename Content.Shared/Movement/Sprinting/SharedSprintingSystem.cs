// SPDX-FileCopyrightText: 2025 August Eymann <august.eymann@gmail.com>
// SPDX-FileCopyrightText: 2025 GoobBot <uristmchands@proton.me>
// SPDX-FileCopyrightText: 2025 gluesniffler <linebarrelerenthusiast@gmail.com>
// SPDX-FileCopyrightText: 2025 Polonium Space <admin@ss14.pl>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Movement.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Events;
using Content.Shared.Damage.Systems;
using Content.Shared.Gravity;
using Content.Shared.Input;
using Content.Shared.Mobs;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Zombies;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using System.Numerics;
using Content.Shared.Alert;
using Content.Shared.CCVar;
using Content.Shared.Movement.Events;
using Content.Shared.Rounding;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Configuration;
using Content.Shared.Humanoid;
using Content.Shared.Jittering;

namespace Content.Shared.Movement.Sprinting;

public abstract class SharedSprintingSystem : EntitySystem
{
    [Dependency] private readonly SharedStaminaSystem _staminaSystem = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeed = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedMoverController _moverController = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly SharedJitteringSystem _jittering = default!;

    public TimeSpan SprintsDelay { get; private set; }

    public override void Initialize()
    {
        SubscribeLocalEvent<SprinterComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<SprinterComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<SprinterComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
        CommandBinds.Builder
            .Bind(ContentKeyFunctions.Sprint, new SprintInputCmdHandler(this))
            .Register<SharedSprintingSystem>();
        SubscribeLocalEvent<SprinterComponent, SprintToggleEvent>(OnSprintToggle);
        SubscribeLocalEvent<SprinterComponent, MobStateChangedEvent>(OnMobStateChangedEvent);
        SubscribeLocalEvent<SprinterComponent, SleepStateChangedEvent>(OnSleep);
        SubscribeLocalEvent<SprinterComponent, ToggleWalkEvent>(OnToggleWalk);
        SubscribeLocalEvent<SprinterComponent, KnockedDownEvent>(OnSprintDisablingEvent);
        SubscribeLocalEvent<SprinterComponent, StunnedEvent>(OnSprintDisablingEvent);
        SubscribeLocalEvent<SprinterComponent, DownedEvent>(OnSprintDisablingEvent);
        SubscribeLocalEvent<CuffableComponent, SprintAttemptEvent>(OnCuffableSprintAttempt);
        SubscribeLocalEvent<StandingStateComponent, SprintAttemptEvent>(OnStandingStateSprintAttempt);
        SubscribeLocalEvent<SprinterComponent, EntityZombifiedEvent>(OnZombified);

        SprintsDelay = TimeSpan.FromSeconds(_cfg.GetCVar(CCVars.SecondsBetweenSprints));

        // TODO: Po wyczerpaniu kondycji dodać jakiś charakterystyczny efekt wizualny, np. duszność po bieganiu.
    }

    #region Core Functions

    private sealed class SprintInputCmdHandler(SharedSprintingSystem system) : InputCmdHandler
    {
        public override bool HandleCmdMessage(IEntityManager entManager, ICommonSession? session, IFullInputCmdMessage message)
        {
            if (session?.AttachedEntity == null)
                return false;

            system.HandleSprintInput(session, message);
            return false;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // We dont add it to the EQE since the comp might get added as this runs.
        var query = EntityQueryEnumerator<SprinterComponent>();
        var curTime = _timing.CurTime;
        while (query.MoveNext(out var uid, out var comp))
        {
            if (curTime < comp.NextUpdate)
                continue;

            comp.NextUpdate = curTime + comp.UpdateRate;

            float delta;
            if (comp.IsSprinting)
            {
                delta = comp.SprintCapacityDrainRate * comp.SprintCapacityDrainMultiplier;
            }
            else if (
                curTime > comp.LastDepleted + TimeSpan.FromSeconds(comp.DelaySecondsAfterDeplete)
                )
            {
                delta = -(comp.SprintCapacityRegenRate * comp.SprintCapacityRegenMultiplier);
            }
            else
            {
                continue;
            }

            ModifySprintCapacity(uid, comp, delta);
        }
    }

    private void OnStartup(Entity<SprinterComponent> ent, ref ComponentStartup args)
    {
        ent.Comp.CurrentSprintCapacity = ent.Comp.SprintCapacity;
        RefreshAlert(ent, ent.Comp);
    }

    private void OnShutdown(Entity<SprinterComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.IsSprinting)
            ToggleSprint(ent, ent.Comp, false);

        _alerts.ClearAlert(ent, ent.Comp.SprintAlert);
    }

    private void OnRefreshSpeed(Entity<SprinterComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (!ent.Comp.IsSprinting)
            return;

        args.ModifySpeed(ent.Comp.SprintSpeedMultiplier);
    }

    private void HandleSprintInput(ICommonSession? session, IFullInputCmdMessage message)
    {
        if (session?.AttachedEntity == null
            || !TryComp<SprinterComponent>(session.AttachedEntity, out var sprinterComponent)
            || !TryComp<InputMoverComponent>(session.AttachedEntity, out var inputMoverComponent)
            || !sprinterComponent.IsSprinting
            // We check this instead of physics so that we can gatekeep sprinting to only work when you are moving intentionally, and not walking.
            && _moverController.GetVelocityInput(inputMoverComponent).Sprinting == Vector2.Zero)
            return;

        if (!sprinterComponent.CanSprint)
        {
            if (message.State == BoundKeyState.Down) // Without this check the message triggers when holding and releasing.
                _popupSystem.PopupClient(Loc.GetString("sprint-disabled"), session.AttachedEntity.Value, session.AttachedEntity.Value, PopupType.Medium);

            return;
        }

        RaiseLocalEvent(session.AttachedEntity.Value, new SprintToggleEvent(!sprinterComponent.IsSprinting && message.State == BoundKeyState.Down));
    }

    private void ModifySprintCapacity(EntityUid uid, SprinterComponent comp, float delta)
    {
        var newValue = comp.CurrentSprintCapacity - delta;
        var clamped = Math.Clamp(newValue, 0f, comp.SprintCapacity);

        if (MathHelper.CloseTo(clamped, comp.CurrentSprintCapacity))
            return;

        comp.CurrentSprintCapacity = clamped;

        if (clamped <= 0f)
        {
            StopSprinting(uid, comp, depleted: true);
        }
        else if (clamped >= comp.SprintThreshold)
        {
            ApplySlowdown(uid, recover: true);
        }

        RefreshAlert(uid, comp);
        Dirty(uid, comp);
    }


    private void OnSprintToggle(EntityUid uid, SprinterComponent component, ref SprintToggleEvent args) =>
        ToggleSprint(uid, component, args.IsSprinting);

    private void ToggleSprint(EntityUid uid, SprinterComponent component, bool isSprinting)
    {
        // Breaking these into two separate if's for better readability
        if (isSprinting == component.IsSprinting)
            return;

        if (isSprinting
            && (!CanSprint(uid, component)
            || _timing.CurTime - component.LastSprint < SprintsDelay))
            return;

        component.LastSprint = _timing.CurTime;
        component.IsSprinting = isSprinting;

        if (isSprinting)
        {
            RaiseLocalEvent(uid, new SprintStartEvent());
            _audio.PlayPredicted(component.SprintStartupSound, uid, uid);
        }

        _movementSpeed.RefreshMovementSpeedModifiers(uid);
        Dirty(uid, component);
    }

    private void StopSprinting(EntityUid uid, SprinterComponent? sprintComp, bool depleted = false)
    {
        if (!Resolve(uid, ref sprintComp))
            return;

        ToggleSprint(uid, sprintComp, false);

        if (depleted)
        {
            var sex = CompOrNull<HumanoidAppearanceComponent>(uid)?.Sex ?? Sex.Unsexed;


            var audioParams = AudioParams.Default.WithVariation(0.1f).AddVolume(-3f);
            var playback = _audio.PlayPredicted(sprintComp.ExhaustedSounds[sex], uid, uid, audioParams);

            ApplySlowdown(uid);
            RaiseLocalEvent(uid, new SprintCapacityDepletedEvent());

            _jittering.DoJitter(uid, TimeSpan.FromSeconds(4f), false, 3f, 6f);

            Dirty(uid, sprintComp);
        }
    }

    private void ApplySlowdown(Entity<SprinterComponent?> ent, bool recover = false)
    {
        if (!Resolve(ent, ref ent.Comp))
            return;

        if (recover)
        {
            RemComp<SlowedDownComponent>(ent);
            RaiseLocalEvent(ent, new SprintCapacityRecoveredEvent());

            return;
        }

        EnsureComp<SlowedDownComponent>(ent, out var comp);

        comp.WalkSpeedModifier = comp.SprintSpeedModifier = ent.Comp.DepletedSpeedModifier;

        ent.Comp.LastDepleted = _timing.CurTime;

        _movementSpeed.RefreshMovementSpeedModifiers(ent);
    }



    private void RefreshAlert(EntityUid uid, SprinterComponent? comp)
    {
        if (!Resolve(uid, ref comp))
            return;

        var level = ContentHelpers.RoundToLevels(
            MathF.Max(0f, comp.SprintCapacity - comp.CurrentSprintCapacity),
            comp.SprintCapacity,
            8);

        _alerts.ShowAlert(uid, comp.SprintAlert, (short)level);
    }

    #endregion

    #region Conditionals

    private bool CanSprint(EntityUid uid, SprinterComponent component)
    {
        if (MathF.Max(0f, component.CurrentSprintCapacity) <= 0f)
            return false;

        // Awaiting on a wizden PR that refactors gravity from whatever the fuck this is.
        if (_gravity.IsWeightless(uid))
        {
            _popupSystem.PopupClient(Loc.GetString("no-sprint-while-weightless"), uid, uid, PopupType.Medium);
            return false;
        }

        var ev = new SprintAttemptEvent();
        RaiseLocalEvent(uid, ref ev);

        return !ev.Cancelled;
    }

    private void OnCuffableSprintAttempt(EntityUid uid, CuffableComponent component, ref SprintAttemptEvent args)
    {
        if (component.CanStillInteract)
            return;

        _popupSystem.PopupClient(Loc.GetString("no-sprint-while-restrained"), uid, uid, PopupType.Medium);
        args.Cancel();
    }

    private void OnStandingStateSprintAttempt(EntityUid uid, StandingStateComponent component, ref SprintAttemptEvent args)
    {
        if (!_standing.IsDown(uid, component))
            return;

        _popupSystem.PopupClient(Loc.GetString("no-sprint-while-lying"), uid, uid, PopupType.Medium);
        args.Cancel();
    }

    #endregion

    #region Misc.Handlers
    private void OnMobStateChangedEvent(EntityUid uid, SprinterComponent component, MobStateChangedEvent args)
    {
        if (!component.IsSprinting
            || args.NewMobState is MobState.Critical or MobState.Dead)
            return;

        ToggleSprint(args.Target, component, false);
    }

    private void OnSleep(EntityUid uid, SprinterComponent component, ref SleepStateChangedEvent args)
    {
        if (!component.IsSprinting
            || !args.FellAsleep)
            return;

        ToggleSprint(uid, component, false);
    }

    private void OnToggleWalk(EntityUid uid, SprinterComponent component, ref ToggleWalkEvent args)
    {
        if (!component.IsSprinting)
            return;

        ToggleSprint(uid, component, false);
    }

    private void OnSprintDisablingEvent<T>(EntityUid uid, SprinterComponent component, ref T args) where T : notnull
    {
        if (!component.IsSprinting)
            return;

        ToggleSprint(uid, component, false);
    }
    private void OnZombified(EntityUid uid, SprinterComponent component, ref EntityZombifiedEvent args) =>
        component.SprintSpeedMultiplier *= 0.5f; // We dont want super fast zombies do we?

    #endregion
}
