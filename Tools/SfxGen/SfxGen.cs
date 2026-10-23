// Procedural SFX generator for AIMLINK. Not part of the Unity project (lives outside Assets/).
// Run Tools/SfxGen/generate.ps1 to (re)write the WAVs into Assets/AimLink/Audio.
// Kept to C# 5 so Windows PowerShell's Add-Type can compile it.
using System;
using System.IO;

public static class SfxGen
{
    const int SR = 48000;
    const double TAU = Math.PI * 2.0;
    static Random rng = new Random(1337);

    sealed class Buf
    {
        public readonly float[] L, R;
        public Buf(double seconds)
        {
            int n = (int)Math.Round(seconds * SR);
            L = new float[n];
            R = new float[n];
        }
        public int N { get { return L.Length; } }
        public void Add(int i, double l, double r)
        {
            if (i < 0 || i >= L.Length) return;
            L[i] += (float)l;
            R[i] += (float)r;
        }
        public void Add(int i, double m) { Add(i, m, m); }
    }

    // Chamberlin state-variable filter. damp = 1/Q. Bp peak gain is 1/damp.
    sealed class Svf
    {
        double lo, bp;
        public double Lp, Bp, Hp;
        public void Step(double x, double fc, double damp)
        {
            double f = 2.0 * Math.Sin(Math.PI * Math.Min(fc, SR / 6.5) / SR);
            Hp = x - lo - damp * bp;
            bp += f * Hp;
            lo += f * bp;
            Lp = lo;
            Bp = bp;
        }
    }

    // ---------- helpers ----------

    static double Noise() { return rng.NextDouble() * 2.0 - 1.0; }
    static double Clamp01(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }
    static double Smooth(double x) { x = Clamp01(x); return x * x * (3.0 - 2.0 * x); }
    static double ExpLerp(double a, double b, double t) { return a * Math.Pow(b / a, Clamp01(t)); }

    // Band-limited saw (additive), phase in cycles.
    static double Saw(double phase, double freq, int maxH)
    {
        int k = (int)(0.45 * SR / Math.Max(freq, 1.0));
        if (k > maxH) k = maxH;
        if (k < 1) k = 1;
        double s = 0;
        for (int h = 1; h <= k; h++) s += Math.Sin(TAU * h * phase) / h;
        return s * 0.6;
    }

    static void NoiseBurst(Buf b, double t0, double dur, double fc, double damp, double amp, double tau)
    {
        Svf fl = new Svf(), fr = new Svf();
        int s = (int)(t0 * SR), n = (int)(dur * SR);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            double e = Math.Min(1.0, t / 0.0005) * Math.Exp(-t / tau) * amp * damp * 3.0;
            fl.Step(Noise(), fc, damp);
            fr.Step(Noise(), fc, damp);
            b.Add(s + i, fl.Bp * e, fr.Bp * e);
        }
    }

    // Sine with a pitch drop: the "thunk" under impacts.
    static void Thump(Buf b, double t0, double f0, double f1, double pitchTau, double ampTau, double amp)
    {
        int s = (int)(t0 * SR), n = (int)(ampTau * 8.0 * SR);
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            double f = f1 + (f0 - f1) * Math.Exp(-t / pitchTau);
            ph += f / SR;
            double e = Math.Min(1.0, t / 0.002) * Math.Exp(-t / ampTau) * amp;
            b.Add(s + i, Math.Sin(TAU * ph) * e);
        }
    }

    // Decaying tone with optional glide f0 -> f1 and a bit of 3rd harmonic.
    static void Blip(Buf b, double t0, double dur, double f0, double f1, double ampTau, double amp, double h3)
    {
        int s = (int)(t0 * SR), n = (int)(dur * SR);
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            ph += ExpLerp(f0, f1, t / dur) / SR;
            double e = Math.Min(1.0, t / 0.002) * Math.Exp(-t / ampTau) * Math.Min(1.0, (dur - t) / 0.003) * amp;
            b.Add(s + i, (Math.Sin(TAU * ph) + h3 * Math.Sin(TAU * 3.0 * ph)) * e);
        }
    }

    // Sample-and-hold noise: digital glitch.
    static void Glitch(Buf b, double t0, double dur, int hold, double amp)
    {
        int s = (int)(t0 * SR), n = (int)(dur * SR);
        double vl = 0, vr = 0;
        for (int i = 0; i < n; i++)
        {
            if (i % hold == 0) { vl = Noise(); vr = Noise(); }
            double e = amp * Math.Min(1.0, (n - i) / 48.0);
            b.Add(s + i, vl * e, vr * e);
        }
    }

    // Small Schroeder reverb, slightly different delays per channel for width.
    static void Reverb(Buf b, double mix, double rt60, double dampHz)
    {
        double[] combMs = { 29.7, 37.1, 41.1, 43.7, 31.3, 47.9 };
        double[] apMs = { 5.0, 1.7 };
        const double apG = 0.7;
        double a = Math.Exp(-TAU * dampHz / SR);
        for (int ch = 0; ch < 2; ch++)
        {
            float[] x = ch == 0 ? b.L : b.R;
            double spread = ch == 0 ? 0.0 : 0.63;
            int n = x.Length;
            double[] wet = new double[n];
            for (int c = 0; c < combMs.Length; c++)
            {
                int d = (int)((combMs[c] + spread) * 0.001 * SR);
                double g = Math.Pow(10.0, -3.0 * (d / (double)SR) / rt60);
                double[] line = new double[d];
                int p = 0;
                double lp = 0;
                for (int i = 0; i < n; i++)
                {
                    double y = line[p];
                    lp = y + (lp - y) * a;
                    line[p] = x[i] + lp * g;
                    wet[i] += y;
                    if (++p == d) p = 0;
                }
            }
            for (int k = 0; k < apMs.Length; k++)
            {
                int d = (int)((apMs[k] + spread * 0.3) * 0.001 * SR);
                double[] line = new double[d];
                int p = 0;
                for (int i = 0; i < n; i++)
                {
                    double z = line[p];
                    double y = z - apG * wet[i];
                    line[p] = wet[i] + apG * y;
                    wet[i] = y;
                    if (++p == d) p = 0;
                }
            }
            for (int i = 0; i < n; i++) x[i] += (float)(mix * 0.2 * wet[i]);
        }
    }

    static double Peak(Buf b)
    {
        double pk = 0;
        for (int i = 0; i < b.N; i++)
        {
            pk = Math.Max(pk, Math.Abs(b.L[i]));
            pk = Math.Max(pk, Math.Abs(b.R[i]));
        }
        return pk;
    }

    static void Scale(Buf b, double g)
    {
        for (int i = 0; i < b.N; i++) { b.L[i] *= (float)g; b.R[i] *= (float)g; }
    }

    static void Save(Buf b, string dir, string name, double peakDb, double drive, bool mono, bool fade)
    {
        int n = b.N;
        if (fade)
        {
            int fi = Math.Min(n / 4, (int)(0.002 * SR)), fo = Math.Min(n / 4, (int)(0.012 * SR));
            for (int i = 0; i < fi; i++) { float g = (float)i / fi; b.L[i] *= g; b.R[i] *= g; }
            for (int i = 0; i < fo; i++) { float g = (float)i / fo; b.L[n - 1 - i] *= g; b.R[n - 1 - i] *= g; }
        }
        if (mono)
        {
            for (int i = 0; i < n; i++) { float m = 0.5f * (b.L[i] + b.R[i]); b.L[i] = m; b.R[i] = m; }
        }
        double pk = Peak(b);
        if (pk < 1e-9 || double.IsNaN(pk)) throw new Exception(name + ": silent or NaN output");
        if (drive > 0)
        {
            Scale(b, 1.0 / pk);
            for (int i = 0; i < n; i++)
            {
                b.L[i] = (float)Math.Tanh(b.L[i] * drive);
                b.R[i] = (float)Math.Tanh(b.R[i] * drive);
            }
            pk = Peak(b);
        }
        Scale(b, Math.Pow(10.0, peakDb / 20.0) / pk);

        double sum = 0;
        for (int i = 0; i < n; i++) sum += b.L[i] * b.L[i] + b.R[i] * b.R[i];
        double rmsDb = 10.0 * Math.Log10(sum / (2.0 * n) + 1e-12);

        int chans = mono ? 1 : 2;
        string path = Path.Combine(dir, name + ".wav");
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            int dataBytes = n * chans * 2;
            w.Write(new char[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);
            w.Write(new char[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)chans);
            w.Write(SR);
            w.Write(SR * chans * 2);
            w.Write((short)(chans * 2));
            w.Write((short)16);
            w.Write(new char[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Round(b.L[i] * 32767.0));
                if (!mono) w.Write((short)Math.Round(b.R[i] * 32767.0));
            }
        }
        Console.WriteLine("{0,-18} {1,5:0.00} s  {2}  peak {3,5:0.0} dB  rms {4,6:0.0} dB",
            name, (double)n / SR, mono ? "mono  " : "stereo", peakDb, rmsDb);
    }

    // ---------- sounds ----------

    // Seamless 4 s idle hum. Every tonal frequency is a multiple of 0.25 Hz so it loops exactly.
    static Buf BootHumLoop()
    {
        const double len = 4.0, xf = 1.0;
        Buf b = new Buf(len);
        int n = b.N, x = (int)(xf * SR);

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            double lfo1 = 1.0 + 0.15 * Math.Sin(TAU * 0.25 * t);
            double lfo2 = 1.0 + 0.2 * Math.Sin(TAU * 0.5 * t + 1.0);
            double baseL = 0, baseR = 0;
            for (int h = 1; h <= 6; h++)
            {
                double a = 1.0 / Math.Pow(h, 1.6);
                baseL += Math.Sin(TAU * 55.0 * h * t + h * 0.7) * a;
                baseR += Math.Sin(TAU * 55.0 * h * t + h * 1.9) * a;
            }
            double fifth = Math.Sin(TAU * 82.5 * t) * 0.25 * lfo2;
            double whineL = (Math.Sin(TAU * 1980.0 * t) + Math.Sin(TAU * 1980.5 * t)) * 0.012;
            double whineR = (Math.Sin(TAU * 1980.0 * t + 1.3) + Math.Sin(TAU * 1980.5 * t + 2.1)) * 0.012;
            b.Add(i, baseL * 0.4 * lfo1 + fifth + whineL, baseR * 0.4 * lfo1 + fifth + whineR);
        }

        // noise bed: render len + xf, then equal-power crossfade the overhang into the head
        int m = n + x;
        double[] nl = new double[m], nr = new double[m];
        Svf fl = new Svf(), fr = new Svf(), al = new Svf(), ar = new Svf();
        for (int i = 0; i < m; i++)
        {
            double wl = Noise(), wr = Noise();
            fl.Step(wl, 500, 1.0); fr.Step(wr, 500, 1.0);
            al.Step(wl, 2500, 0.5); ar.Step(wr, 2500, 0.5);
            nl[i] = fl.Lp * 0.5 + al.Bp * 0.04;
            nr[i] = fr.Lp * 0.5 + ar.Bp * 0.04;
        }
        for (int i = 0; i < n; i++)
        {
            if (i < x)
            {
                double th = (double)i / x * Math.PI * 0.5;
                b.Add(i, nl[i] * Math.Sin(th) + nl[i + n] * Math.Cos(th), nr[i] * Math.Sin(th) + nr[i + n] * Math.Cos(th));
            }
            else b.Add(i, nl[i], nr[i]);
        }
        return b;
    }

    static Buf PostTick()
    {
        Buf b = new Buf(0.05);
        Blip(b, 0, 0.04, 2600, 2200, 0.006, 0.8, 0.2);
        NoiseBurst(b, 0, 0.01, 5000, 0.8, 0.4, 0.002);
        return b;
    }

    static Buf LinkConfirm()
    {
        Buf b = new Buf(1.0);
        Blip(b, 0.0, 0.07, 250, 700, 0.05, 0.4, 0.3);
        Blip(b, 0.08, 0.09, 880, 880, 0.06, 0.7, 0.15);
        Blip(b, 0.19, 0.5, 1320, 1320, 0.14, 0.8, 0.15);
        Blip(b, 0.19, 0.5, 2640, 2640, 0.08, 0.2, 0.0);
        Reverb(b, 0.25, 0.8, 5000);
        return b;
    }

    // Rising resonant sweep with a pulsing scanner tone. Sweep proper lasts ~2.8 s.
    static Buf ScanSweep()
    {
        Buf b = new Buf(3.6);
        const double dur = 2.8;
        Thump(b, 0, 70, 40, 0.1, 0.35, 0.6);
        double ph = 0, phP = 0;
        Svf nl = new Svf(), nr = new Svf();
        for (int i = 0; i < b.N; i++)
        {
            double t = (double)i / SR;
            double p = Clamp01(t / dur);
            double env = Smooth(t / 0.08) * (1.0 - Smooth((t - (dur - 0.5)) / 0.6));
            double fc = ExpLerp(180, 6500, p);
            nl.Step(Noise(), fc, 0.12);
            nr.Step(Noise(), fc, 0.12);
            ph += ExpLerp(110, 1700, p) / SR;
            phP += ExpLerp(9, 30, p) / SR;
            double pulse = 0.6 + 0.4 * Math.Sin(TAU * phP);
            double tone = (Math.Sin(TAU * ph) + 0.3 * Math.Sin(TAU * 2.0 * ph)) * 0.25 * pulse;
            b.Add(i, (nl.Bp * 0.5 + tone) * env, (nr.Bp * 0.5 + tone) * env);
        }
        Reverb(b, 0.3, 1.5, 4000);
        return b;
    }

    static Buf LabelPop()
    {
        Buf b = new Buf(0.14);
        Blip(b, 0, 0.07, 1900, 1300, 0.025, 0.7, 0.1);
        NoiseBurst(b, 0, 0.008, 4000, 0.9, 0.5, 0.0015);
        return b;
    }

    static Buf HudElementIn()
    {
        Buf b = new Buf(0.35);
        Blip(b, 0, 0.1, 500, 2400, 0.08, 0.5, 0.2);
        Blip(b, 0.1, 0.12, 3000, 3000, 0.02, 0.6, 0.0);
        NoiseBurst(b, 0, 0.1, 3000, 0.5, 0.15, 0.04);
        Reverb(b, 0.15, 0.4, 6000);
        return b;
    }

    static Buf ReticleLock()
    {
        Buf b = new Buf(1.0);
        Blip(b, 0, 0.13, 1500, 480, 0.2, 0.45, 0.3);
        NoiseBurst(b, 0.13, 0.06, 3200, 0.5, 0.9, 0.008);
        Thump(b, 0.13, 160, 70, 0.03, 0.07, 0.8);
        Blip(b, 0.14, 0.6, 1760, 1760, 0.12, 0.5, 0.0);
        Blip(b, 0.14, 0.6, 2637, 2637, 0.09, 0.3, 0.0);
        Reverb(b, 0.22, 0.9, 5000);
        return b;
    }

    static Buf TrimTick()
    {
        Buf b = new Buf(0.03);
        Blip(b, 0, 0.02, 3400, 3000, 0.003, 0.8, 0.0);
        return b;
    }

    static Buf TrimReset()
    {
        Buf b = new Buf(0.6);
        Blip(b, 0, 0.22, 1600, 300, 0.15, 0.6, 0.25);
        Blip(b, 0.24, 0.05, 1200, 1200, 0.012, 0.7, 0.1);
        Blip(b, 0.32, 0.05, 1200, 1200, 0.012, 0.7, 0.1);
        Reverb(b, 0.12, 0.4, 6000);
        return b;
    }

    static Buf YPress()
    {
        Buf b = new Buf(0.25);
        Thump(b, 0, 220, 95, 0.03, 0.05, 0.9);
        NoiseBurst(b, 0, 0.01, 2500, 0.8, 0.3, 0.002);
        return b;
    }

    // Sonar-style marker ping: low thunk, then a tone that rings out.
    static Buf BeaconPlace()
    {
        Buf b = new Buf(1.1);
        Thump(b, 0, 180, 80, 0.03, 0.06, 0.7);
        NoiseBurst(b, 0, 0.02, 3000, 0.6, 0.4, 0.004);
        Blip(b, 0.03, 0.9, 1244, 1244, 0.22, 0.6, 0.0);
        Blip(b, 0.03, 0.9, 1866, 1866, 0.12, 0.25, 0.0);
        Reverb(b, 0.3, 1.2, 5000);
        return b;
    }

    static Buf Toggle()
    {
        Buf b = new Buf(0.12);
        Blip(b, 0, 0.03, 1500, 1500, 0.01, 0.6, 0.1);
        Blip(b, 0.045, 0.05, 2250, 2250, 0.015, 0.7, 0.1);
        return b;
    }

    // Servo whir + air as the visor opens, three digital glitches, clunk at the end stop.
    static Buf VisorOpen()
    {
        Buf b = new Buf(2.3);
        const double dur = 1.4;
        double ph = 0;
        Svf lp = new Svf(), al = new Svf(), ar = new Svf();
        for (int i = 0; i < b.N; i++)
        {
            double t = (double)i / SR;
            if (t > dur + 0.05) break;
            double p = Clamp01(t / dur);
            double f = 160.0 + 300.0 * Math.Sin(Math.PI * Math.Pow(p, 0.6));
            ph += f / SR;
            double motor = Saw(ph, f, 16) * (0.8 + 0.2 * Math.Sin(TAU * ph / 5.0));
            lp.Step(motor, 1800, 0.5);
            double env = Smooth(t / 0.05) * (1.0 - Smooth((t - dur + 0.15) / 0.15));
            double airFc = ExpLerp(400, 5000, p);
            al.Step(Noise(), airFc, 0.4);
            ar.Step(Noise(), airFc, 0.4);
            double air = 0.45 * Math.Sin(Math.PI * p);
            b.Add(i, lp.Lp * 0.3 * env + al.Bp * air, lp.Lp * 0.3 * env + ar.Bp * air);
        }
        Glitch(b, 0.35, 0.035, 14, 0.25);
        Glitch(b, 0.62, 0.02, 30, 0.3);
        Glitch(b, 0.95, 0.045, 9, 0.22);
        Thump(b, dur, 120, 55, 0.04, 0.12, 0.7);
        NoiseBurst(b, dur, 0.05, 2200, 0.6, 0.5, 0.01);
        Reverb(b, 0.25, 1.0, 4500);
        return b;
    }

    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        rng = new Random(1337);
        //                                      peak dB drive  mono   fade
        Save(BootHumLoop(),  outDir, "boot_hum_loop", -10.0, 0.0, false, false);
        Save(VisorOpen(),    outDir, "visor_open",     -3.0, 1.2, false, true);
        Save(ScanSweep(),    outDir, "scan_sweep",     -3.0, 1.2, false, true);
        Save(LinkConfirm(),  outDir, "link_confirm",   -3.0, 0.0, false, true);
        Save(ReticleLock(),  outDir, "reticle_lock",   -2.0, 1.2, false, true);
        Save(HudElementIn(), outDir, "hud_element_in", -6.0, 0.0, true,  true);
        Save(PostTick(),     outDir, "post_tick",      -6.0, 0.0, true,  true);
        Save(LabelPop(),     outDir, "label_pop",      -8.0, 0.0, true,  true);
        Save(TrimTick(),     outDir, "trim_tick",     -10.0, 0.0, true,  true);
        Save(TrimReset(),    outDir, "trim_reset",     -6.0, 0.0, true,  true);
        Save(YPress(),       outDir, "y_press",        -4.0, 0.0, true,  true);
        Save(BeaconPlace(),  outDir, "beacon_place",   -4.0, 0.0, false, true);
        Save(Toggle(),       outDir, "toggle",         -8.0, 0.0, true,  true);
    }
}
