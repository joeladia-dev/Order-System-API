using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("AuthDb") ?? "Data Source=auth.db"));
builder.Services.AddHttpClient();

var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var oauthOptions = builder.Configuration.GetSection("OAuth").Get<OAuthOptions>() ?? new OAuthOptions();

if (string.IsNullOrWhiteSpace(authOptions.SigningKey))
{
    throw new InvalidOperationException("Auth:SigningKey is missing. Set ORDER_SYSTEM_JWT_SIGNING_KEY.");
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    db.Database.EnsureCreated();
    EnsureAuthSchema(db);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.MapPost("/api/auth/dev/token", async (DevTokenRequest request, AuthDbContext db) =>
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        if (normalizedEmail is null)
        {
            return Results.BadRequest(new { message = "A valid email is required." });
        }

        var now = DateTimeOffset.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail);
        if (user is null)
        {
            user = new UserEntity
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                EmailVerified = true,
                CreatedAt = now,
                LastSignInAt = now
            };
            db.Users.Add(user);
        }
        else
        {
            user.EmailVerified = true;
            user.LastSignInAt = now;
        }

        await db.SaveChangesAsync();

        var expiresAt = now.AddMinutes(authOptions.AccessTokenMinutes);
        var accessToken = CreateAccessToken(
            user,
            authOptions,
            expiresAt,
            request.Roles,
            request.Scopes);

        return Results.Ok(new
        {
            accessToken,
            tokenType = "Bearer",
            expiresAt,
            user = new
            {
                user.Id,
                user.Email,
                user.EmailVerified
            }
        });
    });
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "AuthService" }));

app.MapGet("/api/auth/session", (HttpContext httpContext) =>
{
    if (!httpContext.Request.Cookies.TryGetValue(authOptions.CookieName, out var accessToken) ||
        string.IsNullOrWhiteSpace(accessToken))
    {
        return Results.Unauthorized();
    }

    var principal = ValidateAccessToken(accessToken, authOptions);
    if (principal is null)
    {
        return Results.Unauthorized();
    }

    var email = principal.FindFirstValue(JwtRegisteredClaimNames.Email)
        ?? principal.FindFirstValue(ClaimTypes.Email)
        ?? string.Empty;
    var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? string.Empty;
    var roles = principal.FindAll(ClaimTypes.Role).Select(x => x.Value).ToArray();
    var scopes = principal.FindFirstValue("scope")?
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? [];
    var expiresAtUnix = principal.FindFirstValue(JwtRegisteredClaimNames.Exp);
    DateTimeOffset? expiresAt = null;
    if (long.TryParse(expiresAtUnix, out var expSeconds))
    {
        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
    }

    return Results.Ok(new
    {
        authenticated = true,
        accessToken,
        tokenType = "Bearer",
        expiresAt,
        user = new
        {
            id = userId,
            email,
            roles,
            scopes
        }
    });
});

app.MapPost("/api/auth/logout", (HttpContext httpContext) =>
{
    var secureCookie = httpContext.Request.IsHttps || !app.Environment.IsDevelopment();
    httpContext.Response.Cookies.Delete(authOptions.CookieName, new CookieOptions
    {
        HttpOnly = true,
        Secure = secureCookie,
        SameSite = SameSiteMode.Lax,
        Path = "/"
    });

    return Results.Ok(new
    {
        signedOut = true
    });
});

app.MapPost("/api/auth/request-code", async (RequestCodeRequest request, AuthDbContext db, IWebHostEnvironment env) =>
{
    var normalizedEmail = NormalizeEmail(request.Email);
    if (normalizedEmail is null)
    {
        return Results.BadRequest(new { message = "A valid email is required." });
    }

    var now = DateTimeOffset.UtcNow;
    var cooldownStart = now.AddSeconds(-authOptions.ResendCooldownSeconds);

    var recentCodes = await db.VerificationCodes
        .Where(x => x.Email == normalizedEmail)
        .ToListAsync();
    var recentCode = recentCodes
        .Where(x => x.CreatedAt >= cooldownStart)
        .OrderByDescending(x => x.CreatedAt)
        .FirstOrDefault();

    if (recentCode is not null)
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    var plainCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    var codeHash = HashCode(plainCode, authOptions.CodePepper);

    db.VerificationCodes.Add(new VerificationCodeEntity
    {
        Id = Guid.NewGuid(),
        Email = normalizedEmail,
        CodeHash = codeHash,
        CreatedAt = now,
        ExpiresAt = now.AddMinutes(authOptions.CodeExpiryMinutes),
        ConsumedAt = null,
        Attempts = 0
    });

    await db.SaveChangesAsync();

    app.Logger.LogInformation("Auth code generated for {Email}. Code: {Code}", normalizedEmail, plainCode);

    return Results.Accepted(value: new
    {
        message = "Verification code generated.",
        expiresInSeconds = authOptions.CodeExpiryMinutes * 60,
        verificationCode = env.IsDevelopment() ? plainCode : null
    });
});

app.MapPost("/api/auth/verify-code", async (VerifyCodeRequest request, AuthDbContext db) =>
{
    var normalizedEmail = NormalizeEmail(request.Email);
    if (normalizedEmail is null || string.IsNullOrWhiteSpace(request.Code))
    {
        return Results.BadRequest(new { message = "Email and code are required." });
    }

    var now = DateTimeOffset.UtcNow;
    var activeCodes = await db.VerificationCodes
        .Where(x => x.Email == normalizedEmail && x.ConsumedAt == null)
        .ToListAsync();
    var code = activeCodes
        .OrderByDescending(x => x.CreatedAt)
        .FirstOrDefault();

    if (code is null || code.ExpiresAt < now)
    {
        return Results.BadRequest(new { message = "Invalid or expired verification code." });
    }

    code.Attempts += 1;
    if (code.Attempts > authOptions.MaxVerifyAttempts)
    {
        code.ConsumedAt = now;
        await db.SaveChangesAsync();
        return Results.BadRequest(new { message = "Verification code no longer valid." });
    }

    var providedHash = HashCode(request.Code.Trim(), authOptions.CodePepper);
    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(code.CodeHash), Convert.FromHexString(providedHash)))
    {
        await db.SaveChangesAsync();
        return Results.BadRequest(new { message = "Invalid or expired verification code." });
    }

    code.ConsumedAt = now;

    var user = await db.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail);
    if (user is null)
    {
        user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            EmailVerified = true,
            CreatedAt = now,
            LastSignInAt = now
        };
        db.Users.Add(user);
    }
    else
    {
        user.EmailVerified = true;
        user.LastSignInAt = now;
    }

    await db.SaveChangesAsync();

    var expiresAt = now.AddMinutes(authOptions.AccessTokenMinutes);
    var accessToken = CreateAccessToken(user, authOptions, expiresAt);

    return Results.Ok(new
    {
        accessToken,
        tokenType = "Bearer",
        expiresAt,
        user = new
        {
            user.Id,
            user.Email,
            user.EmailVerified
        }
    });
});

app.MapGet("/api/auth/oauth/start/{provider}", async (string provider, string? returnUrl, AuthDbContext db) =>
{
    if (!TryGetProvider(provider, oauthOptions, out var providerKey, out var settings, out var providerError))
    {
        return Results.BadRequest(new { message = providerError });
    }

    var normalizedReturnUrl = NormalizeReturnUrl(returnUrl, oauthOptions);
    if (!string.IsNullOrWhiteSpace(returnUrl) && normalizedReturnUrl is null)
    {
        return Results.BadRequest(new { message = "Return URL is not allowed." });
    }

    var now = DateTimeOffset.UtcNow;
    var state = RandomToken(48);
    var expiresAt = now.AddMinutes(oauthOptions.StateExpiryMinutes);

    db.OAuthStates.Add(new OAuthStateEntity
    {
        Id = Guid.NewGuid(),
        Provider = providerKey,
        State = state,
        ReturnUrl = normalizedReturnUrl,
        CreatedAt = now,
        ExpiresAt = expiresAt,
        ConsumedAt = null
    });
    await db.SaveChangesAsync();

    var parameters = new Dictionary<string, string>
    {
        ["response_type"] = "code",
        ["client_id"] = settings.ClientId,
        ["redirect_uri"] = settings.RedirectUri,
        ["scope"] = settings.Scope,
        ["state"] = state
    };

    if (!string.IsNullOrWhiteSpace(settings.Prompt))
    {
        parameters["prompt"] = settings.Prompt;
    }

    var authorizeUrl = BuildUrl(settings.AuthorizationEndpoint, parameters);

    return Results.Ok(new
    {
        provider = providerKey,
        authorizeUrl,
        state,
        expiresAt
    });
});

app.MapGet("/api/auth/oauth/callback/{provider}", async (
    string provider,
    string? code,
    string? state,
    string? error,
    string? error_description,
    HttpContext httpContext,
    AuthDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    if (!TryGetProvider(provider, oauthOptions, out var providerKey, out var settings, out var providerError))
    {
        return Results.BadRequest(new { message = providerError });
    }

    if (!string.IsNullOrWhiteSpace(error))
    {
        return Results.BadRequest(new { message = "OAuth provider returned an error.", error, errorDescription = error_description });
    }

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return Results.BadRequest(new { message = "OAuth callback is missing required parameters." });
    }

    var now = DateTimeOffset.UtcNow;
    var stateRecord = await db.OAuthStates.FirstOrDefaultAsync(x =>
        x.Provider == providerKey && x.State == state && x.ConsumedAt == null);

    if (stateRecord is null || stateRecord.ExpiresAt < now)
    {
        return Results.BadRequest(new { message = "OAuth state is invalid or expired." });
    }

    var httpClient = httpClientFactory.CreateClient();
    var tokenForm = new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = settings.RedirectUri,
        ["client_id"] = settings.ClientId,
        ["client_secret"] = settings.ClientSecret
    };

    using var tokenResponse = await httpClient.PostAsync(settings.TokenEndpoint, new FormUrlEncodedContent(tokenForm));
    var tokenPayloadText = await tokenResponse.Content.ReadAsStringAsync();

    if (!tokenResponse.IsSuccessStatusCode)
    {
        return Results.BadRequest(new { message = "Failed to exchange OAuth authorization code.", status = (int)tokenResponse.StatusCode, details = tokenPayloadText });
    }

    using var tokenPayloadDoc = JsonDocument.Parse(tokenPayloadText);
    var tokenRoot = tokenPayloadDoc.RootElement;

    var providerAccessToken = GetString(tokenRoot, "access_token");
    var providerIdToken = GetString(tokenRoot, "id_token");
    var providerSubject = string.Empty;
    var providerEmail = string.Empty;

    if (!string.IsNullOrWhiteSpace(providerAccessToken) && !string.IsNullOrWhiteSpace(settings.UserInfoEndpoint))
    {
        using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, settings.UserInfoEndpoint);
        userInfoRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", providerAccessToken);

        using var userInfoResponse = await httpClient.SendAsync(userInfoRequest);
        var userInfoText = await userInfoResponse.Content.ReadAsStringAsync();

        if (userInfoResponse.IsSuccessStatusCode)
        {
            using var userInfoDoc = JsonDocument.Parse(userInfoText);
            var userInfo = userInfoDoc.RootElement;

            providerSubject = GetClaimValue(userInfo, settings.SubjectClaim, "sub", "id", "oid");
            providerEmail = GetClaimValue(userInfo, settings.EmailClaim, "email", "preferred_username", "upn");
        }
    }

    if (string.IsNullOrWhiteSpace(providerSubject) || string.IsNullOrWhiteSpace(providerEmail))
    {
        var idTokenClaims = ParseIdTokenClaims(providerIdToken);
        if (string.IsNullOrWhiteSpace(providerSubject))
        {
            providerSubject = GetClaimValue(idTokenClaims, settings.SubjectClaim, "sub", "oid");
        }

        if (string.IsNullOrWhiteSpace(providerEmail))
        {
            providerEmail = GetClaimValue(idTokenClaims, settings.EmailClaim, "email", "preferred_username", "upn");
        }
    }

    var normalizedEmail = NormalizeEmail(providerEmail);
    if (string.IsNullOrWhiteSpace(providerSubject) || normalizedEmail is null)
    {
        return Results.BadRequest(new { message = "OAuth callback did not provide a valid user identity." });
    }

    stateRecord.ConsumedAt = now;

    var externalLogin = await db.ExternalLogins
        .Include(x => x.User)
        .FirstOrDefaultAsync(x => x.Provider == providerKey && x.ProviderSubject == providerSubject);

    UserEntity user;
    if (externalLogin is not null)
    {
        user = externalLogin.User;
        user.Email = normalizedEmail;
    }
    else
    {
        user = await db.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail)
               ?? new UserEntity
               {
                   Id = Guid.NewGuid(),
                   Email = normalizedEmail,
                   CreatedAt = now
               };

        if (user.Id == Guid.Empty)
        {
            user.Id = Guid.NewGuid();
        }

        if (db.Entry(user).State == EntityState.Detached)
        {
            db.Users.Add(user);
        }

        db.ExternalLogins.Add(new ExternalLoginEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Provider = providerKey,
            ProviderSubject = providerSubject,
            CreatedAt = now
        });
    }

    user.EmailVerified = true;
    user.LastSignInAt = now;

    await db.SaveChangesAsync();

    var isAllowlistedAdmin = IsAllowlistedAdmin(normalizedEmail, settings.AdminEmailAllowlist);
    var roleValues = isAllowlistedAdmin
        ? (settings.AdminTokenRoles.Count > 0 ? settings.AdminTokenRoles : ["admin"])
        : (settings.TokenRoles.Count > 0 ? settings.TokenRoles : ["customer"]);
    var scopeValues = isAllowlistedAdmin
        ? (settings.AdminTokenScopes.Count > 0 ? settings.AdminTokenScopes : ["orders.read", "orders.write", "catalog.write", "internal"])
        : (settings.TokenScopes.Count > 0 ? settings.TokenScopes : ["orders.read", "orders.write"]);

    var expiresAt = now.AddMinutes(authOptions.AccessTokenMinutes);
    var accessToken = CreateAccessToken(user, authOptions, expiresAt, roleValues, scopeValues);

    var redirectUrl = stateRecord.ReturnUrl ?? oauthOptions.DefaultReturnUrl;
    if (string.IsNullOrWhiteSpace(redirectUrl))
    {
        redirectUrl = "http://localhost:5173/";
    }

    if (NormalizeReturnUrl(redirectUrl, oauthOptions) is not { } safeRedirectUrl)
    {
        return Results.BadRequest(new { message = "Configured OAuth redirect URL is not allowed." });
    }

    var secureCookie = httpContext.Request.IsHttps || !app.Environment.IsDevelopment();
    httpContext.Response.Cookies.Append(authOptions.CookieName, accessToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = secureCookie,
        SameSite = SameSiteMode.Lax,
        Expires = expiresAt,
        Path = "/"
    });

    var separator = safeRedirectUrl.Contains('?') ? "&" : "?";
    var redirectWithStatus = $"{safeRedirectUrl}{separator}oauth=success&provider={Uri.EscapeDataString(providerKey)}";

    return Results.Redirect(redirectWithStatus);
});

app.Run();

static bool IsAllowlistedAdmin(string normalizedEmail, IReadOnlyCollection<string> allowlist)
{
    if (allowlist.Count == 0)
    {
        return false;
    }

    return allowlist
        .Select(NormalizeEmail)
        .Any(value => string.Equals(value, normalizedEmail, StringComparison.OrdinalIgnoreCase));
}

static string? NormalizeReturnUrl(string? returnUrl, OAuthOptions options)
{
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        return null;
    }

    if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri))
    {
        return null;
    }

    if (uri.Scheme is not ("http" or "https"))
    {
        return null;
    }

    if (options.AllowedReturnOrigins.Count == 0)
    {
        return uri.ToString();
    }

    var origin = uri.GetLeftPart(UriPartial.Authority);
    var isAllowed = options.AllowedReturnOrigins.Any(allowed =>
        string.Equals(allowed.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    return isAllowed ? uri.ToString() : null;
}

static string? NormalizeEmail(string? email)
{
    if (string.IsNullOrWhiteSpace(email)) return null;

    var trimmed = email.Trim().ToLowerInvariant();
    try
    {
        _ = new MailAddress(trimmed);
        return trimmed;
    }
    catch
    {
        return null;
    }
}

static string HashCode(string code, string pepper)
{
    var bytes = Encoding.UTF8.GetBytes($"{code}:{pepper}");
    var hash = SHA256.HashData(bytes);
    return Convert.ToHexString(hash);
}

static string BuildUrl(string baseUrl, IReadOnlyDictionary<string, string> parameters)
{
    var query = string.Join("&", parameters.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    var separator = baseUrl.Contains('?') ? "&" : "?";
    return $"{baseUrl}{separator}{query}";
}

static string RandomToken(int byteLength)
{
    var bytes = RandomNumberGenerator.GetBytes(byteLength);
    return Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

static string? GetString(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        return null;
    }

    return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
}

static string GetClaimValue(JsonElement source, string preferredClaim, params string[] fallbacks)
{
    if (!string.IsNullOrWhiteSpace(preferredClaim))
    {
        var preferred = GetString(source, preferredClaim);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }
    }

    foreach (var fallback in fallbacks)
    {
        var value = GetString(source, fallback);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return string.Empty;
}

static JsonElement ParseIdTokenClaims(string? idToken)
{
    if (string.IsNullOrWhiteSpace(idToken))
    {
        return default;
    }

    try
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(idToken);

        var payloadJson = JsonSerializer.Serialize(token.Payload);
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.Clone();
    }
    catch
    {
        return default;
    }
}

static bool TryGetProvider(
    string provider,
    OAuthOptions options,
    out string providerKey,
    out OAuthProviderOptions settings,
    out string error)
{
    providerKey = provider.Trim().ToLowerInvariant();
    settings = new OAuthProviderOptions();
    error = string.Empty;

    if (string.IsNullOrWhiteSpace(providerKey))
    {
        error = "OAuth provider is required.";
        return false;
    }

    if (!options.Providers.TryGetValue(providerKey, out var providerOptions))
    {
        error = "Unsupported OAuth provider.";
        return false;
    }

    if (!providerOptions.Enabled)
    {
        error = "OAuth provider is not enabled.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(providerOptions.ClientId) ||
        string.IsNullOrWhiteSpace(providerOptions.ClientSecret) ||
        string.IsNullOrWhiteSpace(providerOptions.AuthorizationEndpoint) ||
        string.IsNullOrWhiteSpace(providerOptions.TokenEndpoint) ||
        string.IsNullOrWhiteSpace(providerOptions.RedirectUri))
    {
        error = "OAuth provider is missing required configuration.";
        return false;
    }

    settings = providerOptions;
    return true;
}

static void EnsureAuthSchema(AuthDbContext db)
{
    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS OAuthStates (
    Id TEXT NOT NULL CONSTRAINT PK_OAuthStates PRIMARY KEY,
    Provider TEXT NOT NULL,
    State TEXT NOT NULL,
    ReturnUrl TEXT NULL,
    CreatedAt TEXT NOT NULL,
    ExpiresAt TEXT NOT NULL,
    ConsumedAt TEXT NULL
);");

    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ExternalLogins (
    Id TEXT NOT NULL CONSTRAINT PK_ExternalLogins PRIMARY KEY,
    UserId TEXT NOT NULL,
    Provider TEXT NOT NULL,
    ProviderSubject TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    CONSTRAINT FK_ExternalLogins_Users_UserId FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE CASCADE
);");

    db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_OAuthStates_Provider_State ON OAuthStates (Provider, State);");
    db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalLogins_Provider_ProviderSubject ON ExternalLogins (Provider, ProviderSubject);");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ExternalLogins_UserId ON ExternalLogins (UserId);");
}

static string CreateAccessToken(
    UserEntity user,
    AuthOptions options,
    DateTimeOffset expiresAt,
    IReadOnlyCollection<string>? roles = null,
    IReadOnlyCollection<string>? scopes = null)
{
    var roleValues = roles?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        ?? ["customer"];
    var scopeValues = scopes?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        ?? ["orders.read", "orders.write"];

    var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new(JwtRegisteredClaimNames.Email, user.Email)
    };

    claims.AddRange(roleValues.Select(role => new Claim(ClaimTypes.Role, role)));
    claims.Add(new Claim("scope", string.Join(' ', scopeValues)));

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: options.Issuer,
        audience: options.Audience,
        claims: claims,
        notBefore: DateTime.UtcNow,
        expires: expiresAt.UtcDateTime,
        signingCredentials: credentials);

    return new JwtSecurityTokenHandler().WriteToken(token);
}

static ClaimsPrincipal? ValidateAccessToken(string accessToken, AuthOptions options)
{
    var tokenHandler = new JwtSecurityTokenHandler();
    var parameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey))
    };

    try
    {
        var principal = tokenHandler.ValidateToken(accessToken, parameters, out _);
        return principal;
    }
    catch
    {
        return null;
    }
}

sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<VerificationCodeEntity> VerificationCodes => Set<VerificationCodeEntity>();
    public DbSet<OAuthStateEntity> OAuthStates => Set<OAuthStateEntity>();
    public DbSet<ExternalLoginEntity> ExternalLogins => Set<ExternalLoginEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>()
            .HasIndex(x => x.Email)
            .IsUnique();

        modelBuilder.Entity<VerificationCodeEntity>()
            .HasIndex(x => new { x.Email, x.CreatedAt });

        modelBuilder.Entity<OAuthStateEntity>()
            .HasIndex(x => new { x.Provider, x.State })
            .IsUnique();

        modelBuilder.Entity<ExternalLoginEntity>()
            .HasIndex(x => new { x.Provider, x.ProviderSubject })
            .IsUnique();

        modelBuilder.Entity<ExternalLoginEntity>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

sealed class UserEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
}

sealed class VerificationCodeEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public int Attempts { get; set; }
}

sealed class OAuthStateEntity
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

sealed class ExternalLoginEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderSubject { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public UserEntity User { get; set; } = null!;
}

sealed record RequestCodeRequest(string Email);
sealed record VerifyCodeRequest(string Email, string Code);
sealed record DevTokenRequest(string Email, IReadOnlyCollection<string>? Roles, IReadOnlyCollection<string>? Scopes);

sealed class AuthOptions
{
    public string Issuer { get; set; } = "OrderSystem.Auth";
    public string Audience { get; set; } = "OrderSystem.Api";
    public string CookieName { get; set; } = "order_system_auth";
    public string SigningKey { get; set; } = string.Empty;
    public string CodePepper { get; set; } = "dev-otp-pepper";
    public int AccessTokenMinutes { get; set; } = 60;
    public int CodeExpiryMinutes { get; set; } = 10;
    public int ResendCooldownSeconds { get; set; } = 30;
    public int MaxVerifyAttempts { get; set; } = 5;
}

sealed class OAuthOptions
{
    public int StateExpiryMinutes { get; set; } = 10;
    public string DefaultReturnUrl { get; set; } = "http://localhost:5173/";
    public List<string> AllowedReturnOrigins { get; set; } = ["http://localhost:5173"];
    public Dictionary<string, OAuthProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

sealed class OAuthProviderOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string UserInfoEndpoint { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string Scope { get; set; } = "openid profile email";
    public string Prompt { get; set; } = "select_account";
    public string EmailClaim { get; set; } = "email";
    public string SubjectClaim { get; set; } = "sub";
    public List<string> TokenRoles { get; set; } = ["customer"];
    public List<string> TokenScopes { get; set; } = ["orders.read", "orders.write"];
    public List<string> AdminEmailAllowlist { get; set; } = [];
    public List<string> AdminTokenRoles { get; set; } = ["admin"];
    public List<string> AdminTokenScopes { get; set; } = ["orders.read", "orders.write", "catalog.write", "internal"];
}
