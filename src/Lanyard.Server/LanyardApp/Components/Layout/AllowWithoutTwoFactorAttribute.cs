namespace Lanyard.App.Components.Layout;

// Marks a page RouteAuthorizationGate still shows to a signed-in user whose company requires
// two-factor authentication they haven't set up yet. Every other page, anonymous ones included
// (the home page is anonymous so visitors get the public homepage), is swapped for the set-up
// screen. Reserved for the sign-in/sign-out flow, legal pages and full-screen device displays.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AllowWithoutTwoFactorAttribute : Attribute;
