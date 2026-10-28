using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>Motes, bubbles, pops, splashes, steam and falling droplets of the liquid style (DESIGN 2a.3).</summary>
public sealed partial class LiquidModel
{
    /// <summary>Alive rate-emitted motes are capped at Soft 6, below S 120 16, Max 48, otherwise 32.</summary>
    public int MoteCap => _effects == EffectsIntensity.Soft ? 6
        : _size < 120 ? 16
        : _effects == EffectsIntensity.Max ? 48
        : 32;

    /// <summary>Motes, bubbles and gold motes alive.</summary>
    public int MoteCount => _moteCount;

    public int PopCount => _popCount;

    public int SplashCount => _splashCount;

    public int SteamCount => _steamCount;

    /// <summary>Motes spawned by the emission rate since construction (for tests and benchmarks).</summary>
    internal long EmittedMotes { get; private set; }

    internal long EmittedSteam { get; private set; }

    /// <summary>Every bubble spawned (rate, impacts, sign of life), gold motes excluded.</summary>
    internal long SpawnedBubbles { get; private set; }

    internal long SpawnedSplashes { get; private set; }

    private int RegularMotes()
    {
        var count = 0;
        for (var i = 0; i < _moteCount; i++)
        {
            if (_motes[i].Kind != MoteKind.Gold)
            {
                count++;
            }
        }

        return count;
    }

    private void EmitMotes(double h, double o, double dt)
    {
        if (!Active)
        {
            _moteDebt.Reset();
            return;
        }

        var rate = _effects == EffectsIntensity.Soft
            ? 0.6 + 3 * h
            : (1.5 + 12 * h + (_tier == SpeedTier.Redline ? 6 + 10 * o : 0)) * (_effects == EffectsIntensity.Max ? 1.6 : 1);
        var n = _moteDebt.Take(rate, dt);
        if (n == 0)
        {
            return;
        }

        var alive = RegularMotes();
        var cap = MoteCap;
        for (var i = 0; i < n && alive < cap; i++)
        {
            if (SpawnBubble(h, o, MoteKind.Bubble, double.NaN, double.NaN))
            {
                alive++;
                EmittedMotes++;
            }
        }
    }

    private void EmitSignOfLife()
    {
        if (_signOfLifeAge is not { } age || Active || _unavailable || _effects < EffectsIntensity.Soft || _reduceMotion)
        {
            return;
        }

        var fresh = _lastSignOfLifeAge is not { } last || age < last;
        if (fresh && age < 0.3)
        {
            // One cool bubble from the bottom-left (the prototype's x = −0.08 S, heat 0.15 for its speed and size).
            SpawnBubble(0.15, 0, MoteKind.Cool, -0.08, double.NaN);
        }
    }

    private bool SpawnBubble(double h, double o, byte kind, double x, double y)
    {
        if (_moteCount >= MotePool)
        {
            return false;
        }

        if (!double.IsFinite(x))
        {
            x = _random.Range(-0.6, 0.6) * R;
        }

        if (!double.IsFinite(y))
        {
            y = Math.Sqrt(Math.Max(0, R * R - x * x)) - _random.Range(0.02, 0.06);
        }

        ref var p = ref _motes[_moteCount++];
        p = default;
        p.Kind = kind;
        p.X = x;
        p.Y = y;
        p.Vy = -(0.08 + 0.35 * h + 0.25 * o) * _random.Range(0.7, 1.3);
        p.Size = Math.Max(0.9 / _size, (0.006 + 0.01 * h) * _random.Range(0.6, 1.4));
        p.Phase = _random.Next() * Tau;
        SpawnedBubbles++;
        return true;
    }

    private void StepMotes(double h, double dt)
    {
        var accel = 0.12 + 0.2 * h;
        var wobble = Tau * 2.3 * dt;
        for (var i = 0; i < _moteCount;)
        {
            ref var p = ref _motes[i];
            p.Age += dt;
            p.Vy -= accel * dt;
            p.Y += p.Vy * dt;
            p.Phase += wobble;
            if (p.Phase >= Tau)
            {
                p.Phase -= Tau;
            }

            var x = p.X + Math.Sin(p.Phase) * 0.006;
            var surface = SurfaceY(x);
            if (p.Y - p.Size < surface || p.Y < -R)
            {
                if (p.Y >= -R)
                {
                    var size = p.Size;
                    AddPop(x, surface, size);
                    if (_boiling && _random.Next() < 0.55)
                    {
                        SpawnSplashes(x, surface, _random.Next() < 0.5 ? 1 : 2, 0.5);
                    }
                }

                _motes[i] = _motes[--_moteCount];
                continue;
            }

            i++;
        }
    }

    private void AddPop(double x, double y, double size)
    {
        if (_popCount >= PopPool)
        {
            return;
        }

        ref var p = ref _pops[_popCount++];
        p = default;
        p.X = x;
        p.Y = y;
        p.Size = size;
        p.Life = PopDuration;
    }

    private void StepPops(double dt)
    {
        for (var i = 0; i < _popCount;)
        {
            ref var p = ref _pops[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _pops[i] = _pops[--_popCount];
                continue;
            }

            i++;
        }
    }

    private void SpawnSplashes(double x, double y, int count, double speed)
    {
        for (var i = 0; i < count && _splashCount < SplashPool; i++)
        {
            ref var p = ref _splashes[_splashCount++];
            p = default;
            p.X = x;
            p.Y = y - 0.004;
            p.Vx = _random.Range(-0.25, 0.25);
            p.Vy = -_random.Range(0.35, 0.7) * (0.6 + 0.6 * speed);
            p.Life = _random.Range(0.3, 0.6);
            p.Size = _random.Range(0.006, 0.011);
            SpawnedSplashes++;
        }
    }

    private void StepSplashes(double dt)
    {
        for (var i = 0; i < _splashCount;)
        {
            ref var p = ref _splashes[i];
            p.Age += dt;
            p.Vy += 2.2 * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            if (p.Age >= p.Life || (p.Vy > 0 && p.Y > SurfaceY(p.X)))
            {
                _splashes[i] = _splashes[--_splashCount];
                continue;
            }

            i++;
        }
    }

    private void StepSteam(double o, double dt)
    {
        if (_boiling && _size >= 120)
        {
            var n = _steamDebt.Take((2 + 4 * o) * (_effects == EffectsIntensity.Max ? 1.6 : 1), dt);
            for (var i = 0; i < n && _steamCount < SteamPool; i++)
            {
                ref var p = ref _steam[_steamCount++];
                p = default;
                p.X = _random.Range(-0.25, 0.25);
                p.Vy = -_random.Range(0.06, 0.12);
                p.Life = _random.Range(0.8, 1.4);
                p.Size = _random.Range(0.03, 0.06);
                p.Phase = _random.Next() * Tau;
                EmittedSteam++;
            }
        }
        else
        {
            _steamDebt.Reset();
        }

        for (var i = 0; i < _steamCount;)
        {
            ref var p = ref _steam[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _steam[i] = _steam[--_steamCount];
                continue;
            }

            // Y is relative to the mean surface, so wisps ride the level.
            p.Y += p.Vy * dt;
            p.X += Math.Sin(p.Phase + p.Age * 3) * 0.02 * dt;
            i++;
        }
    }

    private void StepDroplets(double h, double o, double dt)
    {
        for (var i = 0; i < _dropletCount;)
        {
            ref var d = ref _droplets[i];
            d.Vy += 2.6 * dt;
            d.Y += d.Vy * dt;
            var surface = SurfaceY(d.X);
            if (d.Y >= surface)
            {
                var x = d.X;
                var strength = d.Strength;
                _droplets[i] = _droplets[--_dropletCount];
                Impact(x, surface, strength, h, o);
                continue;
            }

            i++;
        }
    }

    private void Impact(double x, double surface, double strength, double h, double o)
    {
        AddRipple(x, 0.022 * (0.6 + 0.6 * strength));
        var standard = _effects >= EffectsIntensity.Standard;
        if (standard)
        {
            SpawnSplashes(x, surface, 3 + (int)Math.Round(3 * Math.Min(1, strength)), 1);
        }

        var bubbles = standard ? 6 : 2;
        for (var i = 0; i < bubbles; i++)
        {
            if (!standard && RegularMotes() >= MoteCap)
            {
                break;
            }

            var bx = x + _random.Range(-0.03, 0.03);
            var by = surface + _random.Range(0.02, 0.08);
            SpawnBubble(h, o, MoteKind.Bubble, bx, by);
        }
    }

    private void FillSprites()
    {
        var n = 0;
        for (var i = 0; i < _moteCount; i++)
        {
            ref readonly var p = ref _motes[i];
            var x = p.X + Math.Sin(p.Phase) * 0.006;
            var fade = ReadoutZone.Fade(x, p.Y - _readoutShift);
            _sprites[n++] = p.Kind == MoteKind.Gold
                ? new LiquidSprite(LiquidSpriteKind.GoldMote, x, p.Y, p.Size, EnergyPalette.Gold, fade, 1)
                : new LiquidSprite(LiquidSpriteKind.Mote, x, p.Y, p.Size, _moteGlow, fade, p.Kind == MoteKind.Cool ? 0 : _moteHot);
        }

        _moteEnd = n;
        for (var i = 0; i < _popCount; i++)
        {
            ref readonly var p = ref _pops[i];
            var u = p.Age / p.Life;
            var fade = ReadoutZone.Fade(p.X, p.Y - _readoutShift);
            _sprites[n++] = new LiquidSprite(LiquidSpriteKind.Pop, p.X, p.Y, p.Size * (1 + 1.5 * u), _popColor, 0.6 * (1 - u) * fade, 0);
        }

        _popEnd = n;
        for (var i = 0; i < _splashCount; i++)
        {
            ref readonly var p = ref _splashes[i];
            var u = p.Age / p.Life;
            var fade = ReadoutZone.Fade(p.X, p.Y - _readoutShift);
            _sprites[n++] = new LiquidSprite(LiquidSpriteKind.Splash, p.X, p.Y, p.Size, _splashColor, 0.9 * (1 - 0.6 * u) * fade, 0);
        }

        _splashEnd = n;
        var yL = MeanSurfaceY;
        var steamAlpha = _dark ? 0.12 : 0.18;
        for (var i = 0; i < _steamCount; i++)
        {
            ref readonly var p = ref _steam[i];
            var u = p.Age / p.Life;
            var y = yL + p.Y;
            var alpha = Math.Sin(Math.PI * u) * steamAlpha * ReadoutZone.Fade(p.X, y - _readoutShift);
            _sprites[n++] = new LiquidSprite(LiquidSpriteKind.Steam, p.X, y, p.Size * (1 + u), _steamColor, alpha, 0);
        }

        _steamEnd = n;
        for (var i = 0; i < _dropletCount; i++)
        {
            ref readonly var d = ref _droplets[i];
            _sprites[n++] = new LiquidSprite(LiquidSpriteKind.Droplet, d.X, d.Y, 0.012 + 0.006 * d.Strength, d.Color, 0.95, 0);
        }

        _spriteCount = n;
    }
}
