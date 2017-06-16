﻿/**** Git Credential Manager for Windows ****
 *
 * Copyright (c) GitHub Corporation
 * All rights reserved.
 *
 * MIT License
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the """"Software""""), to deal
 * in the Software without restriction, including without limitation the rights to
 * use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
 * the Software, and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
 * FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
 * COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN
 * AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."
**/

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Alm.Authentication;
using Git = Microsoft.Alm.Git;

namespace GitHub.Authentication
{
    internal class Authority: IAuthority
    {
        /// <summary>
        /// The GitHub required HTTP accepts header value
        /// </summary>
        public const string GitHubApiAcceptsHeaderValue = "application/vnd.github.v3+json";

        /// <summary>
        /// The maximum wait time for a network request before timing out
        /// </summary>
        public const int RequestTimeout = 15 * 1000; // 15 second limit

        public Authority(TargetUri targetUri)
        {
            // The GitHub proper API endpoints
            if (targetUri.Host == "github.com")
            {
                _authorityUrl = "https://api.github.com/authorizations";
                _validationUrl = "https://api.github.com/user/subscriptions";
            }
            else
            {
                // If we're here, it's GitHub Enterprise via a configured authority
                var baseUrl = targetUri.ActualUri.GetLeftPart(UriPartial.Authority);
                _authorityUrl = baseUrl + "/api/v3/authorizations";
                _validationUrl = baseUrl + "/api/v3/user/subscriptions";
            }
        }

        private readonly string _validationUrl, _authorityUrl;

        public async Task<AuthenticationResult> AcquireToken(
            TargetUri targetUri,
            string username,
            string password,
            string authenticationCode,
            TokenScope scope,
            string tokenDescription = null)
        {
            const string GitHubOptHeader = "X-GitHub-OTP";

            Token token = null;

            using (HttpClientHandler handler = targetUri.HttpClientHandler)
            using (HttpClient httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(RequestTimeout)
            })
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", Global.UserAgent);
                httpClient.DefaultRequestHeaders.Add("Accept", GitHubApiAcceptsHeaderValue);

                string basicAuthValue = String.Format("{0}:{1}", username, password);
                byte[] authBytes = Encoding.UTF8.GetBytes(basicAuthValue);
                basicAuthValue = Convert.ToBase64String(authBytes);

                httpClient.DefaultRequestHeaders.Add("Authorization", "Basic " + basicAuthValue);

                if (!String.IsNullOrWhiteSpace(authenticationCode))
                {
                    httpClient.DefaultRequestHeaders.Add(GitHubOptHeader, authenticationCode);
                }

                if (String.IsNullOrEmpty(tokenDescription))
                {
                    tokenDescription = "git: " + targetUri;
                }

                const string HttpJsonContentType = "application/x-www-form-urlencoded";
                const string JsonContentFormat = @"{{ ""scopes"": {0}, ""note"": ""{1} on {2} at {3:dd-MMM-yyyy HH:mm}"" }}";

                StringBuilder scopesBuilder = new StringBuilder();
                scopesBuilder.Append('[');

                foreach (var item in scope.ToString().Split(' '))
                {
                    scopesBuilder.Append("\"")
                                 .Append(item)
                                 .Append("\"")
                                 .Append(", ");
                }

                // remove trailing ", "
                if (scopesBuilder.Length > 0)
                {
                    scopesBuilder.Remove(scopesBuilder.Length - 2, 2);
                }

                scopesBuilder.Append(']');

                string jsonContent = String.Format(JsonContentFormat, scopesBuilder, tokenDescription, Environment.MachineName, DateTime.Now);

                using (StringContent content = new StringContent(jsonContent, Encoding.UTF8, HttpJsonContentType))
                using (HttpResponseMessage response = await httpClient.PostAsync(_authorityUrl, content))
                {
                    Git.Trace.WriteLine($"server responded with {response.StatusCode}.");

                    switch (response.StatusCode)
                    {
                        case HttpStatusCode.OK:
                        case HttpStatusCode.Created:
                            {
                                string responseText = await response.Content.ReadAsStringAsync();

                                Match tokenMatch;
                                if ((tokenMatch = Regex.Match(responseText, @"\s*""token""\s*:\s*""([^""]+)""\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)).Success
                                    && tokenMatch.Groups.Count > 1)
                                {
                                    string tokenText = tokenMatch.Groups[1].Value;
                                    token = new Token(tokenText, TokenType.Personal);
                                }

                                if (token == null)
                                {
                                    Git.Trace.WriteLine($"authentication for '{targetUri}' failed.");
                                    return new AuthenticationResult(GitHubAuthenticationResultType.Failure);
                                }
                                else
                                {
                                    Git.Trace.WriteLine($"authentication success: new personal acces token for '{targetUri}' created.");
                                    return new AuthenticationResult(GitHubAuthenticationResultType.Success, token);
                                }
                            }

                        case HttpStatusCode.Unauthorized:
                            {
                                if (String.IsNullOrWhiteSpace(authenticationCode)
                                    && response.Headers.Any(x => String.Equals(GitHubOptHeader, x.Key, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var mfakvp = response.Headers.First(x => String.Equals(GitHubOptHeader, x.Key, StringComparison.OrdinalIgnoreCase) && x.Value != null && x.Value.Count() > 0);

                                    if (mfakvp.Value.First().Contains("app"))
                                    {
                                        Git.Trace.WriteLine($"two-factor app authentication code required for '{targetUri}'.");
                                        return new AuthenticationResult(GitHubAuthenticationResultType.TwoFactorApp);
                                    }
                                    else
                                    {
                                        Git.Trace.WriteLine($"two-factor sms authentication code required for '{targetUri}'.");
                                        return new AuthenticationResult(GitHubAuthenticationResultType.TwoFactorSms);
                                    }
                                }
                                else
                                {
                                    Git.Trace.WriteLine($"authentication failed for '{targetUri}'.");
                                    return new AuthenticationResult(GitHubAuthenticationResultType.Failure);
                                }
                            }

                        default:
                            Git.Trace.WriteLine($"authentication failed for '{targetUri}'.");
                            return new AuthenticationResult(GitHubAuthenticationResultType.Failure);
                    }
                }
            }
        }

        public async Task<bool> ValidateCredentials(TargetUri targetUri, Credential credentials)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);
            BaseSecureStore.ValidateCredential(credentials);

            string authString = String.Format("{0}:{1}", credentials.Username, credentials.Password);
            byte[] authBytes = Encoding.UTF8.GetBytes(authString);
            string authEncode = Convert.ToBase64String(authBytes);

            // craft the request header for the GitHub v3 API w/ credentials
            using (HttpClientHandler handler = targetUri.HttpClientHandler)
            using (HttpClient httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(RequestTimeout)
            })
            {
                httpClient.DefaultRequestHeaders.Add("User-Agent", Global.UserAgent);
                httpClient.DefaultRequestHeaders.Add("Accept", GitHubApiAcceptsHeaderValue);
                httpClient.DefaultRequestHeaders.Add("Authorization", "Basic " + authEncode);

                using (HttpResponseMessage response = await httpClient.GetAsync(_validationUrl))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        Git.Trace.WriteLine($"credential validation for '{targetUri}' succeeded.");
                        return true;
                    }
                    else
                    {
                        Git.Trace.WriteLine($"credential validation for '{targetUri}' failed.");
                        return false;
                    }
                }
            }
        }
    }
}
