namespace Pulaski.Edge;

/// <summary>The edge's own configuration (section <c>Edge</c>): where Pulaski is, who it signs in as, and its secrets.</summary>
public sealed class EdgeOptions
{
    public const string Section = "Edge";

    /// <summary>The Pulaski API, such as <c>https://pulaski.example.com/</c>.</summary>
    public Uri ApiUrl { get; set; } = new("http://localhost:5080/");

    /// <summary>The edge node's id, from its page in Pulaski (Configure → Edge nodes).</summary>
    public Guid EdgeNodeId { get; set; }

    /// <summary>The service user it signs in as: a local user holding the Edge node role at the node's site.</summary>
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>The SQLite file it buffers in. Losing it loses only what wasn't forwarded yet.</summary>
    public string BufferPath { get; set; } = "pulaski-edge.db";

    /// <summary>
    /// Where OPC UA keeps the edge's application certificate and trust lists (#22): <c>own</c>, <c>trusted</c>, <c>issuer</c>,
    /// <c>rejected</c>. Copy a server's certificate into <c>trusted/certs</c> to trust it, or give its thumbprint on the connection.
    /// </summary>
    public string PkiPath { get; set; } = "pki";

    /// <summary>Secrets per connection code, such as <c>{ "BROKER1": { "password": "..." } }</c>; never sent to Pulaski.</summary>
    public Dictionary<string, Dictionary<string, string>> Connections { get; set; } = [];

    public TimeSpan ConfigRefresh { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A machine speed is forwarded at most this often; states, modes, stop codes and counts as soon as they change.</summary>
    public TimeSpan SpeedInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A process value is forwarded at least this often while it holds steady or stays within its deadband (#26).</summary>
    public TimeSpan ProcessValueHeartbeat { get; set; } = TimeSpan.FromMinutes(1);
    public int BatchSize { get; set; } = 500;
}
