﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Vita.Common;
using Vita.Entities;
using Vita.Entities.Web;
using Vita.Modules.WebClient;
using Vita.Modules.EncryptedData;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Vita.Modules.OAuthClient {

  public partial class OAuthClientModule : IOAuthClientService {

    // Standard response to get-access-token endpoint
    public class AccessTokenResponse {
      [Node("access_token")]
      public string AccessToken;
      [Node("expires_in")]
      public long ExpiresIn;
      [Node("token_type")]
      public string TokenType;
      [Node("refresh_token")]
      public string RefreshToken;
      [Node("id_token")]
      public string IdToken; //Open ID connect only
    }

    /// <summary>The query (parameters) portion of authorization URL - a page on OAuth server 
    /// that user is shown to approve the access by the client app. </summary>
    public const string AuthorizationUrlQuery = "?response_type=code&client_id={0}&redirect_uri={1}&scope={2}&state={3}";
    /// <summary>The query (parameters) portion; sent either as part of URL or as form-url-encoded body.</summary>
    public const string AccessTokenUrlQueryTemplate =
      "code={0}&redirect_uri={1}&grant_type=authorization_code&access_type=offline";
    public const string AccessTokenUrlQueryTemplateWithClientInfo =
      "code={0}&redirect_uri={1}&grant_type=authorization_code&access_type=offline&client_id={2}&client_secret={3}";
    public const string RefreshTokenUrlQueryTemplate =
      "refresh_token={0}&grant_type=refresh_token";
    public const string RefreshTokenUrlQueryTemplateWithClientInfo =
      "refresh_token={0}&grant_type=refresh_token&client_id={1}&client_secret={2}";
    public const string OpenIdClaimsParameter = "claims={0}";

    #region IAuthClientService
    public event AsyncEvent<RedirectEventArgs> Redirected;

    public async Task OnRedirected(OperationContext context, string state, string authCode, string error) {
      var session = context.OpenSystemSession();
      Guid reqId;
      Util.Check(Guid.TryParse(state, out reqId), "Invalid state parameter ({0}), expected GUID.", state);
      var flow = session.GetEntity<IOAuthClientFlow>(reqId);
      Util.Check(flow != null, "OAuth Redirect: invalid state parameter, OAuth flow not found.", state);
      CheckFlowExpired(flow); 
      flow.AuthorizationCode = authCode;
      flow.Error = error;
      flow.Status = string.IsNullOrWhiteSpace(error) ? OAuthFlowStatus.Authorized : OAuthFlowStatus.Error;
      session.SaveChanges();
      if(Redirected != null) {
        var args = new RedirectEventArgs(context, flow.Id);
        await Redirected.RaiseAsync(this, args);
      }
    }

    public async Task<Guid> RetrieveAccessToken(OperationContext context, Guid flowId) {
      var session = context.OpenSystemSession();
      var flow = session.GetEntity<IOAuthClientFlow>(flowId);
      Util.Check(flow != null, "OAuth client flow not found, ID: {0}", flowId);
      CheckFlowExpired(flow);
      string err = null;
      switch(flow.Status) {
        case OAuthFlowStatus.Started: err = "Access not authorized yet."; break;
        case OAuthFlowStatus.TokenRetrieved: err = "Authorization code already used to retrieve token."; break;
        case OAuthFlowStatus.Error: err = "Authorization failed or denied - " + flow.Error; break;
      }
      Util.Check(err == null, "Cannot retrieve token: {0}.", err);
      Util.CheckNotEmpty(flow.AuthorizationCode, "Authorization code not retrieved, cannot retrieve access token.");

      var apiClient = new WebApiClient(flow.Account.Server.TokenRequestUrl, ClientOptions.Default, badRequestContentType: typeof(string));
      var clientSecret = flow.Account.ClientSecret.DecryptString(this.Settings.EncryptionChannel);
      var server = flow.Account.Server;
      var serverOptions = server.Options;
      // Some servers expect clientId/secret in auth header
      string query;
      if(serverOptions.IsSet(OAuthServerOptions.RequestTokenClientInfoInAuthHeader)) {
        var clientInfo = flow.Account.ClientIdentifier + ":" + clientSecret;
        var encClientInfo = StringHelper.Base64Encode(clientInfo);
        apiClient.AddAuthorizationHeader(encClientInfo, "Basic");
        query = StringHelper.FormatUri(AccessTokenUrlQueryTemplate, flow.AuthorizationCode, flow.RedirectUrl);
      } else {
        //others - clientId/secret in URL
        query = StringHelper.FormatUri(AccessTokenUrlQueryTemplateWithClientInfo, flow.AuthorizationCode, 
            flow.RedirectUrl, flow.Account.ClientIdentifier, clientSecret);
      }
      //Make a call; standard is POST, but some servers use GET (LinkedIn)
      AccessTokenResponse tokenResp;
      if(serverOptions.IsSet(OAuthServerOptions.TokenUseGet)) {
        //GET, no body
        tokenResp = await apiClient.GetAsync<AccessTokenResponse>("?" + query);
      } else {
          var formContent = CreateFormUrlEncodedContent(query);
          tokenResp = await apiClient.PostAsync<HttpContent, AccessTokenResponse>(formContent, string.Empty);
      }
      flow.Status = OAuthFlowStatus.TokenRetrieved;
      //LinkedIn returns milliseconds here - it's a bug, reported. So here is workaround
      var expIn = tokenResp.ExpiresIn;
      if(expIn > 1e+9) //if more than one billion, it is milliseconds
        expIn = expIn / 1000;
      var expires = this.App.TimeService.UtcNow.AddSeconds(expIn);
      OAuthTokenType tokenType;
      var ok = Enum.TryParse<OAuthTokenType>(tokenResp.TokenType, true, out tokenType); //should be Bearer
      // Create AccessToken entity
      var accessToken = flow.Account.NewOAuthAccessToken(flow.UserId, tokenResp.AccessToken, tokenType,
          tokenResp.RefreshToken, tokenResp.IdToken, flow.Scopes, App.TimeService.UtcNow, expires, Settings.EncryptionChannel);
      // Unpack OpenId id_token - it is JWT token
      if(serverOptions.IsSet(OAuthServerOptions.OpenIdConnect) && !string.IsNullOrWhiteSpace(tokenResp.IdToken)) {
        var payload = OpenIdConnectUtil.GetJwtPayload(tokenResp.IdToken);
        var idTkn = Settings.JsonDeserializer.Deserialize<OpenIdToken>(payload);
        accessToken.NewOpenIdToken(idTkn, payload);
      }
      session.SaveChanges(); 
      return accessToken.Id;
    }

    private static HttpContent CreateFormUrlEncodedContent(string content) {
      var bytes = Encoding.ASCII.GetBytes(content);
      var stream = new System.IO.MemoryStream(bytes);
      HttpContent cnt = new StreamContent(stream);
      cnt.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
      return cnt;
    }

    public async Task<bool> RefreshAccessToken(OperationContext context, Guid tokenId) {
      var session = context.OpenSystemSession();
      var token = session.GetEntity<IOAuthAccessToken>(tokenId);
      Util.Check(token != null, "Token not found, ID: {0}.", tokenId); 
      Util.Check(token.RefreshToken != null, "RefreshToken value is empty, cannot refresh access token.");
      var acct = token.Account;
      var apiClient = new WebApiClient(acct.Server.TokenRefreshUrl, ClientOptions.Default, badRequestContentType: typeof(string));
      var clientSecret = acct.ClientSecret.DecryptString(this.Settings.EncryptionChannel);
      var server = acct.Server;
      var serverOptions = server.Options;
      var strRtoken = token.RefreshToken.DecryptString(Settings.EncryptionChannel);
      // Some servers expect clientId/secret in auth header
      string query;
      if(serverOptions.IsSet(OAuthServerOptions.RequestTokenClientInfoInAuthHeader)) {
        var clientInfo = acct.ClientIdentifier + ":" + clientSecret;
        var encClientInfo = StringHelper.Base64Encode(clientInfo);
        apiClient.AddAuthorizationHeader(encClientInfo, "Basic");
        query = StringHelper.FormatUri(RefreshTokenUrlQueryTemplate, strRtoken);
      } else {
        //others - clientId/secret in URL
        query = StringHelper.FormatUri(RefreshTokenUrlQueryTemplateWithClientInfo, strRtoken, acct.ClientIdentifier, clientSecret);
      }
      //Make a call; standard is POST, but some servers use GET (LinkedIn)
      AccessTokenResponse tokenResp;
      if(serverOptions.IsSet(OAuthServerOptions.TokenUseGet)) {
        //GET, no body
        tokenResp = await apiClient.GetAsync<AccessTokenResponse>("?" + query);
      } else {
        var formContent = CreateFormUrlEncodedContent(query);
        tokenResp = await apiClient.PostAsync<HttpContent, AccessTokenResponse>(formContent, string.Empty);
      }
      // Update token info
      token.AccessToken = session.NewOrUpdate(token.AccessToken, tokenResp.AccessToken, Settings.EncryptionChannel);
      // A new refresh token might be returned (should in fact)
      if (!string.IsNullOrEmpty(tokenResp.RefreshToken))
        token.RefreshToken = session.NewOrUpdate(token.RefreshToken, tokenResp.RefreshToken, Settings.EncryptionChannel);
      var utcNow = this.App.TimeService.UtcNow;
      token.ExpiresOn = utcNow.AddSeconds(tokenResp.ExpiresIn);
      token.RefreshedOn = utcNow; 
      session.SaveChanges(); 
      return await Task.FromResult(true); 
    }

    public async Task<TProfile> GetBasicProfile<TProfile>(OperationContext context, Guid tokenId) {
      var session = context.OpenSystemSession();
      var token = session.GetEntity<IOAuthAccessToken>(tokenId); 
      Util.Check(token != null, "AccessToken may not be null.");
      var server = token.Account.Server;
      string profileUrl = server.BasicProfileUrl;
      Util.CheckNotEmpty(profileUrl, "Basic profile URL for OAuth server {0} is empty, cannot retrieve profile.", server.Name);
      var profileUri = new Uri(profileUrl);
      var webClient = new WebApiClient(profileUrl, ClientOptions.Default, typeof(string));
      webClient.SetupForOAuth(token);
      var profile = await webClient.GetAsync<TProfile>(string.Empty);
      return profile;
    }

    public async Task<string> GetBasicProfile(OperationContext context, Guid tokenId) {
      var session = context.OpenSystemSession();
      var token = session.GetEntity<IOAuthAccessToken>(tokenId);
      Util.Check(token != null, "AccessToken may not be null.");
      var server = token.Account.Server; 
      string profileUrl = server.BasicProfileUrl;
      Util.CheckNotEmpty(profileUrl, "Basic profile URL for OAuth server {0} is empty, cannot retrieve profile.", server.Name);
      var profileUri = new Uri(profileUrl);
      var webClient = new WebApiClient(profileUrl, ClientOptions.Default, typeof(string));
      webClient.SetupForOAuth(token); 
      var respStream = await webClient.GetAsync<System.IO.Stream>(string.Empty);
      var reader = new System.IO.StreamReader(respStream);
      var profile = reader.ReadToEnd();
      return profile;
    }

    #endregion

    private void CheckFlowExpired(IOAuthClientFlow flow) {
      var session = EntityHelper.GetSession(flow);
      var expires = flow.CreatedOn.AddMinutes(Settings.FlowExpirationMinutes);
      var now = App.TimeService.UtcNow;
      if (expires < now) {
        flow.Status = OAuthFlowStatus.Expired;
        session.SaveChanges(); 
        session.Context.ThrowIf(true, ClientFaultCodes.InvalidAction, "OAuthFlow", "OAuth 2.0 process expired.");
      }
    }
  } //class
}
