namespace VectorNNTP.NNTPD.Moderation;

/// <summary>
/// Resolved moderator route for one newsgroup: expected <c>Approved:</c> identity and
/// authorized AUTHINFO principal.
/// </summary>
/// <param name="Pattern">Wildmat that matched the newsgroup.</param>
/// <param name="Address">Expected <c>Approved:</c> mailbox identity.</param>
/// <param name="Username">AUTHINFO username authorized to inject that approval.</param>
public readonly record struct ModeratorIdentity(string Pattern, string Address, string Username);
