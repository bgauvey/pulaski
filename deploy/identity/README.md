# Signing in through an identity provider

Pulaski can let people sign in through your organisation's OpenID Connect provider: Microsoft Entra ID, AD FS (2016 or later), Keycloak,
Okta or any other that publishes a discovery document (`/.well-known/openid-configuration`). Pulaski passwords keep working beside it, at
least for administrators, so you can always get back in (see [Recovery](#recovery)).

How it works (D-091):

- Pulaski's **API** is the OpenID Connect client: a confidential client using the authorization code flow with PKCE. The provider's
  settings and the client secret live in the API's configuration only, never in the database or the UI.
- The **UI** sends the browser to the provider and receives it back at `/account/oidc/callback`, then hands the answer to the API, which
  redeems the code, checks the ID token (signature from the provider's published keys, issuer, audience = your client id, expiry, nonce)
  and starts the usual Pulaski session.
- People are known by the provider's **issuer and subject** (`sub`), never by name or e-mail. Name and e-mail are copied from the ID token
  at every sign-in.
- **Roles stay in Pulaski.** Someone signing in for the first time is added with no role and sees *you have no role yet*; an administrator
  then gives them one on **Configure → Users**, where they are marked **Awaiting a role**.

## 1. Register Pulaski with the provider

Create a web application (confidential client) with:

| Setting | Value |
| --- | --- |
| Redirect URI | `https://<your Pulaski UI>/account/oidc/callback` (for trying it on your machine: `http://localhost:5090/account/oidc/callback`) |
| Grant / flow | Authorization code, with PKCE (S256) |
| Scopes | `openid profile email` |
| Client authentication | Client secret, sent in the request body (`client_secret_post`) |

Note the **client id**, the **client secret** and the **issuer** (authority) address.

### Microsoft Entra ID

1. **Entra admin center → App registrations → New registration.** Supported account types: *this organizational directory only*.
   Redirect URI: *Web*, `https://<ui>/account/oidc/callback`.
2. **Certificates & secrets → New client secret.** Copy its value.
3. **Token configuration → Add optional claim → ID → `email`** (and `upn` if your users have no mailbox).
4. Authority: `https://login.microsoftonline.com/<tenant id>/v2.0`. Use your tenant id, **never `common` or `organizations`**: the
   issuer must be one tenant's for the ID token to be checked.
5. Entra ID doesn't send `email_verified`; if `email` is empty for your users, set `EmailClaim` to `preferred_username`.

### AD FS

1. **AD FS Management → Application Groups → Add → Server application accessing a web API** (or *Web browser accessing a web
   application* with a server application). Redirect URI as above; generate a shared secret.
2. Permit the `openid`, `profile` and `email` scopes, and issue `name` and `email` in the ID token with claim rules.
3. Authority: `https://<adfs host>/adfs`.

### Keycloak

1. **Clients → Create client**: OpenID Connect, *Client authentication* on, *Standard flow* on, *Direct access grants* off.
2. Valid redirect URIs: `https://<ui>/account/oidc/callback`. Under *Advanced*, set *Proof Key for Code Exchange* to `S256`.
3. **Credentials** tab: copy the client secret.
4. Authority: `https://<keycloak host>/realms/<realm>`.

### Okta

1. **Applications → Create App Integration → OIDC → Web Application.** Sign-in redirect URI as above; grant type *Authorization Code*.
2. Copy the client id and secret. Assign the application to the people or groups who may use Pulaski.
3. Authority: `https://<your okta domain>/oauth2/default` (or your custom authorization server).

## 2. Configure the API

Under `Security:Providers:<name>` in the API's settings, environment variables or secret store; the name appears in the sign-in address
(`/account/oidc/<name>`):

```json
{
  "Security": {
    "LocalSignIn": "everyone",
    "Providers": {
      "contoso": {
        "DisplayName": "Contoso (Entra ID)",
        "Authority": "https://login.microsoftonline.com/<tenant id>/v2.0",
        "ClientId": "<client id>",
        "ClientSecret": "<client secret>",
        "Scopes": "openid profile email",
        "NameClaim": "name",
        "EmailClaim": "email"
      }
    }
  }
}
```

As environment variables: `Security__Providers__contoso__ClientSecret=...`. Keep the secret out of `appsettings.json` in production.
A provider without an authority, client id and secret isn't offered. Restart the API after changing these; the UI needs no settings.

The sign-in page then shows **Sign in with Contoso (Entra ID)** above the password form.

## 3. Let people in

1. Ask each person to sign in once with the provider. They see that they have no role yet.
2. On **Configure → Users**, they are listed as **Awaiting a role**. Click **Change**, add their role and site, and ask them to sign in again.

Switching someone off in Pulaski keeps them out even when the provider lets them in. Taking them out of the provider (or the application's
assignment) stops new sign-ins; a session already running lasts until it ends (12 hours at most).

## 4. Passwords only for administrators (optional)

Set `Security:LocalSignIn` to `administrators` once everyone signs in through the provider. Then a Pulaski password works only for:

- **administrators**, so someone can always get in to fix the configuration;
- **service accounts**: machines such as the edge (accounts created with the *Edge node* role are marked so automatically; others are
  marked on **Configure → Users**).

Everyone else is told to use your organisation's sign-in.

## Recovery

- The provider is down or misconfigured: sign in with an administrator's Pulaski password; it always works.
- No administrator password is known: on an empty database the API creates one from `Security:BootstrapAdmin`. Otherwise reset one in the
  database as for any lost password, or ask another administrator.
- Check the API's log for *refused the sign-in* (the provider's error code) or *couldn't be trusted* (the ID token failed a check:
  usually a wrong authority, a tenant mismatch or a clock far off).

## Password reset by e-mail (optional)

People with a Pulaski password can reset a forgotten one themselves (D-093). It is off until you set, in the API's settings:

```json
{
  "Security": {
    "PasswordReset": { "Enabled": true, "UiBaseUrl": "https://mes.example.com", "LinkMinutes": 30 }
  },
  "Email": {
    "Host": "smtp.example.com",
    "Port": 587,
    "UseTls": true,
    "Username": "pulaski@example.com",
    "Password": "<from your secret store>",
    "From": "pulaski@example.com",
    "FromName": "Pulaski"
  }
}
```

- `UiBaseUrl` is the address people open Pulaski at; the link is built from it, never from the request.
- The e-mail goes to the address on the user in Pulaski, so check that administrators keep those right.
- Service accounts never get links; with `LocalSignIn` = `administrators`, only administrators do.
- A message the server refuses is logged (*Couldn't send …*) and not retried; the person can ask again after a minute.
