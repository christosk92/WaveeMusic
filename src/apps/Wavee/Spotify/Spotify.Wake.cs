// The two OS facts that say the network may be back: a wake from sleep (the engine's PowerSession) and an address change
// (System.Net.NetworkInformation, NotifyAddrChange — NativeAOT-safe). Both land on OS worker threads and only ever ask the
// session for a LinksKick (Spotify.Session.cs Kick): the fold decides whether any link was waiting, so a signal that
// arrives while everything is Up changes nothing.
//
// NOT NetworkAvailabilityChanged / GetIsNetworkAvailable (Hyper-V's vEthernet, a VPN and loopback-ish adapters keep it
// "available" through an outage), and no INetworkListManager (COM for no extra fact a reconnect needs).
using FluentGpu.WindowsApi.Power;
using System.Net.NetworkInformation;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>The wake and address-change subscriptions that kick the session's waiting links. One adapter change fires
    /// several address events and a resume often precedes DHCP by a second or two, so every signal re-arms ONE debounce
    /// timer and the kick goes out once it settles; <see cref="KickLimiter"/> then caps a flapping adapter.</summary>
    public static class WakeKicks
    {
        /// <summary>Quiet time after the last signal before the kick goes out.</summary>
        public const int DebounceMs = 1_500;

        static Timer? s_debounce;
        static string s_reason = "";
        static readonly Lock Gate = new();
        static bool s_installed;

        /// <summary>Subscribe. Idempotent. A second subscriber to <c>PowerSession.Resumed</c>: the playback power policy holds
        /// the live <c>PowerSession.Subscribe()</c>, and a handler attached beside its own is still raised.</summary>
        internal static void Install()
        {
            lock (Gate)
            {
                if (s_installed) return;
                s_installed = true;
            }
            try
            {
                PowerSession.Resumed += OnResumed;
                NetworkChange.NetworkAddressChanged += OnAddressChanged;
            }
            catch (Exception ex) { Log.Warn("spotify", "wake/network kick subscribe failed", ex); }
        }

        internal static void Shutdown()
        {
            try { PowerSession.Resumed -= OnResumed; } catch { }
            try { NetworkChange.NetworkAddressChanged -= OnAddressChanged; } catch { }
            lock (Gate)
            {
                s_installed = false;
                s_debounce?.Dispose();
                s_debounce = null;
            }
        }

        // Both handlers run on an OS worker thread: they only re-arm the timer; the hop to the shell thread is Kick's.
        static void OnResumed() => Arm("resumed");

        static void OnAddressChanged(object? sender, EventArgs e) => Arm("address-changed");

        static void Arm(string reason)
        {
            lock (Gate)
            {
                if (!s_installed) return;
                s_reason = reason;
                if (s_debounce is null) s_debounce = new Timer(static _ => Fire(), null, DebounceMs, Timeout.Infinite);
                else s_debounce.Change(DebounceMs, Timeout.Infinite);
            }
        }

        static void Fire()
        {
            string reason;
            lock (Gate) reason = s_reason;
            Kick(reason);
        }
    }
}
