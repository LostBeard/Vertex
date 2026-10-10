using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpawnDev.Minecraft.Auth;

/// <summary>What to show the player so they can approve the sign-in in a browser.</summary>
public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt)
{
    /// <summary>A link that pre-fills the code.</summary>
    public string DirectLink => "https://www.microsoft.com/link?otc=" + Uri.EscapeDataString(UserCode);
    public override string ToString() => $"To sign in, open {VerificationUri} and enter the code {UserCode} (or open {DirectLink})";
}

/// <summary>
/// Signs a Microsoft account into Minecraft Java Edition without an app registration of our own: live.com device
/// code sign-in under a public Minecraft title's client id, then Xbox Live user/device/title tokens (requests signed
/// with an ECDSA P-256 proof key), XSTS for Minecraft services, and login_with_xbox. This is the same path the
/// open-source minecraft-protocol / prismarine-auth clients use by default. Tokens are cached in an
/// <see cref="IAccountStore"/>, refreshed silently, and the device code is only needed again when the
/// Microsoft refresh token is gone or revoked.
/// </summary>
public sealed class MicrosoftAuth
{
    /// <summary>Minecraft for Nintendo Switch's public live.com client id (prismarine-auth's default "authTitle").</summary>
    public const string ClientId = "00000000441cc96b";
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";

    private readonly IAccountStore _store;
    private readonly Func<DeviceCodePrompt, Task> _onDeviceCode;
    private readonly HttpClient _http;

    public MicrosoftAuth(IAccountStore store, Func<DeviceCodePrompt, Task> onDeviceCode, HttpClient? http = null)
    {
        _store = store;
        _onDeviceCode = onDeviceCode;
        // Cookies matter: live.com ties the device-code poll to a cookie set by the code request.
        _http = http ?? new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private sealed class State
    {
        public string? MsaAccessToken { get; set; }
        public string? MsaRefreshToken { get; set; }
        public DateTimeOffset MsaExpiresAt { get; set; }
        public string? ProofKeyPkcs8 { get; set; }
        public string? MinecraftToken { get; set; }
        public DateTimeOffset MinecraftExpiresAt { get; set; }
        public Guid ProfileId { get; set; }
        public string? ProfileName { get; set; }
        public ProfileKeys? Keys { get; set; }
    }

    /// <summary>
    /// Returns a profile with a Minecraft access token, from the cache when it is still good, else by refreshing,
    /// else by a new device-code sign-in (which calls onDeviceCode and waits for the player to approve).
    /// </summary>
    public async Task<GameProfile> SignInAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (state.MinecraftToken != null && state.ProfileName != null && state.MinecraftExpiresAt > now.AddMinutes(10))
        {
            if (state.Keys == null || state.Keys.RefreshAfter <= now)
            {
                state.Keys = await FetchKeysAsync(state.MinecraftToken, cancellationToken);
                await SaveAsync(state, cancellationToken);
            }
            return new GameProfile(state.ProfileId, state.ProfileName, state.MinecraftToken, state.Keys);
        }

        var msa = await GetMicrosoftTokenAsync(state, cancellationToken);
        using var key = LoadOrCreateProofKey(state);
        await SaveAsync(state, cancellationToken);

        var deviceToken = await XboxAsync("https://device.auth.xboxlive.com/device/authenticate", key, new
        {
            Properties = new
            {
                AuthMethod = "ProofOfPossession",
                Id = "{" + Guid.NewGuid() + "}",
                DeviceType = "Nintendo",
                SerialNumber = "{" + Guid.NewGuid() + "}",
                Version = "0.0.0",
                ProofKey = Jwk(key),
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, contractVersion: "1", cancellationToken);
        var titleToken = await XboxAsync("https://title.auth.xboxlive.com/title/authenticate", key, new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                DeviceToken = deviceToken.Token,
                RpsTicket = "t=" + msa,
                SiteName = "user.auth.xboxlive.com",
                ProofKey = Jwk(key),
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, contractVersion: "1", cancellationToken);
        var userToken = await XboxAsync("https://user.auth.xboxlive.com/user/authenticate", key, new
        {
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "t=" + msa,
            },
        }, contractVersion: "2", cancellationToken);
        var xsts = await XboxAsync("https://xsts.auth.xboxlive.com/xsts/authorize", key, new
        {
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
            Properties = new
            {
                UserTokens = new[] { userToken.Token },
                DeviceToken = deviceToken.Token,
                TitleToken = titleToken.Token,
                ProofKey = Jwk(key),
                SandboxId = "RETAIL",
            },
        }, contractVersion: "1", cancellationToken);
        var userHash = xsts.Json["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>()
            ?? throw new InvalidOperationException("XSTS response has no user hash");

        using (var login = MinecraftRequest(HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox", null))
        {
            login.Content = JsonContent(new { identityToken = $"XBL3.0 x={userHash};{xsts.Token}" });
            var json = await SendJsonAsync(login, "Minecraft login_with_xbox", cancellationToken);
            state.MinecraftToken = json["access_token"]!.GetValue<string>();
            state.MinecraftExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]!.GetValue<int>());
        }
        using (var profile = MinecraftRequest(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile", state.MinecraftToken))
        {
            using var response = await _http.SendAsync(profile, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("this Microsoft account does not own Minecraft Java Edition (no Java profile)");
            var json = await ReadJsonAsync(response, "Minecraft profile", cancellationToken);
            state.ProfileId = Guid.ParseExact(json["id"]!.GetValue<string>(), "N");
            state.ProfileName = json["name"]!.GetValue<string>();
        }
        state.Keys = await FetchKeysAsync(state.MinecraftToken, cancellationToken);
        await SaveAsync(state, cancellationToken);
        return new GameProfile(state.ProfileId, state.ProfileName!, state.MinecraftToken, state.Keys);
    }

    /// <summary>The account's chat signing key pair, issued by Mojang (valid ~48 h, refresh after ~40 h).</summary>
    private async Task<ProfileKeys> FetchKeysAsync(string minecraftToken, CancellationToken ct)
    {
        using var request = MinecraftRequest(HttpMethod.Post, "https://api.minecraftservices.com/player/certificates", minecraftToken);
        var json = await SendJsonAsync(request, "Minecraft player certificates", ct);
        var pair = json["keyPair"]!;
        return new ProfileKeys(
            ProfileKeys.PemBody(pair["privateKey"]!.GetValue<string>()),
            ProfileKeys.PemBody(pair["publicKey"]!.GetValue<string>()),
            Convert.FromBase64String(json["publicKeySignatureV2"]!.GetValue<string>()),
            DateTimeOffset.Parse(json["expiresAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(json["refreshedAfter"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Forgets the account (the next sign-in needs the device code again).</summary>
    public Task SignOutAsync(CancellationToken cancellationToken = default) => _store.DeleteAsync(cancellationToken);

    // ---------------------------------------------------------------- live.com

    private async Task<string> GetMicrosoftTokenAsync(State state, CancellationToken ct)
    {
        if (state.MsaAccessToken != null && state.MsaExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return state.MsaAccessToken;
        if (state.MsaRefreshToken != null)
        {
            using var refresh = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_token.srf")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["scope"] = Scope,
                    ["client_id"] = ClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = state.MsaRefreshToken,
                }),
            };
            using var response = await _http.SendAsync(refresh, ct);
            if (response.IsSuccessStatusCode)
            {
                ApplyMsaToken(state, JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!);
                return state.MsaAccessToken!;
            }
            // Revoked or expired (e.g. password change): fall through to a new device-code sign-in.
            state.MsaRefreshToken = null;
        }
        return await DeviceCodeAsync(state, ct);
    }

    private async Task<string> DeviceCodeAsync(State state, CancellationToken ct)
    {
        JsonNode code;
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_connect.srf")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["scope"] = Scope,
                ["client_id"] = ClientId,
                ["response_type"] = "device_code",
            }),
        })
        {
            code = await SendJsonAsync(request, "live.com device code", ct);
        }
        var deviceCode = code["device_code"]!.GetValue<string>();
        var interval = TimeSpan.FromSeconds(Math.Max(1, code["interval"]?.GetValue<int>() ?? 5));
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(code["expires_in"]!.GetValue<int>());
        await _onDeviceCode(new DeviceCodePrompt(code["user_code"]!.GetValue<string>(), code["verification_uri"]!.GetValue<string>(), expiresAt));

        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(interval, ct);
            using var poll = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_token.srf?client_id=" + ClientId)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
            };
            using var response = await _http.SendAsync(poll, ct);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            var error = json["error"]?.GetValue<string>();
            if (error == null)
            {
                ApplyMsaToken(state, json);
                return state.MsaAccessToken!;
            }
            if (error == "authorization_pending") continue;
            if (error == "slow_down") { interval += TimeSpan.FromSeconds(5); continue; }
            throw new InvalidOperationException($"Microsoft sign-in failed: {error} {json["error_description"]?.GetValue<string>()}");
        }
        throw new TimeoutException("the sign-in code expired before it was approved");
    }

    private static void ApplyMsaToken(State state, JsonNode json)
    {
        state.MsaAccessToken = json["access_token"]!.GetValue<string>();
        state.MsaRefreshToken = json["refresh_token"]?.GetValue<string>() ?? state.MsaRefreshToken;
        state.MsaExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- Xbox Live

    private sealed record XboxToken(string Token, JsonNode Json);

    private async Task<XboxToken> XboxAsync(string url, ECDsa key, object payload, string contractVersion, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, MustRevalidate = true, NoCache = true };
        request.Headers.Add("x-xbl-contract-version", contractVersion);
        request.Headers.Add("Signature", Sign(key, new Uri(url).AbsolutePath, body));
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var xerr = response.Headers.TryGetValues("x-err", out var v) ? v.FirstOrDefault() : null;
            if (xerr == null && text.Length > 0)
                try { xerr = JsonNode.Parse(text)?["XErr"]?.ToString(); } catch (JsonException) { }
            throw new InvalidOperationException(XboxError(xerr) ?? $"Xbox Live {new Uri(url).Host} failed: {(int)response.StatusCode} {text}");
        }
        var json = JsonNode.Parse(text)!;
        return new XboxToken(json["Token"]!.GetValue<string>(), json);
    }

    /// <summary>
    /// Xbox Live's request signature: version 1, a Windows FILETIME timestamp, and an ES256 (IEEE P1363) signature
    /// over "POST", the path, the (empty) authorization header and the body, each NUL-terminated.
    /// </summary>
    internal static string Sign(ECDsa key, string pathAndQuery, ReadOnlySpan<byte> body, long? unixSeconds = null)
    {
        var filetime = ((unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + 11644473600L) * 10000000L;
        using var signed = new MemoryStream();
        Span<byte> b8 = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(b8, 1);
        signed.Write(b8[..4]);
        signed.WriteByte(0);
        BinaryPrimitives.WriteInt64BigEndian(b8, filetime);
        signed.Write(b8);
        signed.WriteByte(0);
        foreach (var part in new[] { "POST", pathAndQuery, "" })
        {
            signed.Write(Encoding.ASCII.GetBytes(part));
            signed.WriteByte(0);
        }
        signed.Write(body);
        signed.WriteByte(0);
        var signature = key.SignData(signed.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var header = new byte[12 + signature.Length];
        BinaryPrimitives.WriteInt32BigEndian(header, 1);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(4), filetime);
        signature.CopyTo(header, 12);
        return Convert.ToBase64String(header);
    }

    private static object Jwk(ECDsa key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        return new
        {
            kty = "EC",
            x = Base64Url(p.Q.X!),
            y = Base64Url(p.Q.Y!),
            crv = "P-256",
            alg = "ES256",
            use = "sig",
        };
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ECDsa LoadOrCreateProofKey(State state)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (state.ProofKeyPkcs8 != null) key.ImportPkcs8PrivateKey(Convert.FromBase64String(state.ProofKeyPkcs8), out _);
        else state.ProofKeyPkcs8 = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        return key;
    }

    private static string? XboxError(string? xerr) => xerr switch
    {
        "2148916227" => "Xbox: this account is banned from Xbox Live.",
        "2148916229" => "Xbox: this account is restricted and a guardian has not allowed online play (account.microsoft.com/family).",
        "2148916233" => "Xbox: this account has no Xbox profile yet. Create one at https://signup.live.com/signup (or sign in to xbox.com once).",
        "2148916234" => "Xbox: this account has not accepted the Xbox terms of service. Sign in to xbox.com once and accept them.",
        "2148916235" => "Xbox: Xbox Live is not available in this account's region.",
        "2148916236" or "2148916237" => "Xbox: this account needs adult verification (sign in at https://login.live.com).",
        "2148916238" => "Xbox: this account is under 18 and must be added to a family by an adult.",
        _ => null,
    };

    // ---------------------------------------------------------------- helpers

    private static HttpRequestMessage MinecraftRequest(HttpMethod method, string url, string? bearer)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("MinecraftLauncher/2.2.10675"); // what minecraft-protocol sends
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private static ByteArrayContent JsonContent(object payload)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<JsonNode> SendJsonAsync(HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        return await ReadJsonAsync(response, what, ct);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        // Error bodies from these endpoints describe the failure; they do not echo tokens back.
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{what} failed: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text) ?? throw new InvalidDataException($"{what}: empty response");
    }

    private async Task<State> LoadAsync(CancellationToken ct)
    {
        var json = await _store.LoadAsync(ct);
        return json == null ? new State() : JsonSerializer.Deserialize<State>(json) ?? new State();
    }

    private Task SaveAsync(State state, CancellationToken ct) => _store.SaveAsync(JsonSerializer.Serialize(state), ct);
}
