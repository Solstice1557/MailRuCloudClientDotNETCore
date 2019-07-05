//-----------------------------------------------------------------------------------------------
// <copyright file="Account.cs" company="Erast Korolev">
//     Created in 2018, just under by MIT license. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------------------------------

namespace MailRuCloudClient
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Runtime.InteropServices;
    using System.Security;
    using System.Threading.Tasks;
    
    using MailRuCloudClient.Data;
    using MailRuCloudClient.Data.Rates;
    using MailRuCloudClient.Exceptions;
    using MailRuCloudClient.Static;
    using MailRuCloudClient.Static.Helpers;

    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Defines the MAILRU account.
    /// </summary>
    public class Account
    {
        /// <summary>
        /// Cookie container for account.
        /// </summary>
        private CookieContainer cookies = new CookieContainer();

        private readonly SecureString password;

        /// <summary>
        /// Initializes a new instance of the <see cref="Account" /> class.
        /// </summary>
        /// <param name="email">Login as email.</param>
        /// <param name="password">Password related with this login.</param>
        public Account(string email, SecureString password)
        {
            Debug.Assert(!string.IsNullOrEmpty(email), "Is null or empty.");
            Debug.Assert(password != null && password.Length > 0, "Is null or empty.");

            this.Email = email;
            this.password = password;
        }

        /// <summary>
        /// Gets or sets login as email.
        /// </summary>
        public string Email { get; }

        /// <summary>
        /// Gets the list of activated tariffs for account. Relogin the account to recalculation connected tariffs.
        /// </summary>
        public List<Rate> ActivatedTariffs { get; private set; }

        /// <summary>
        /// Gets a value indicating whether is turn on upload size limit on 2GB for account.
        /// </summary>
        public bool Has2GBUploadSizeLimit { get => !this.ActivatedTariffs.Any(x => x.Id != "ZERO"); }

        /// <summary>
        /// Gets authorization token.
        /// </summary>
        internal string AuthToken { get; private set; }

        /// <summary>
        /// Gets or sets the account cookies.
        /// </summary>
        internal CookieContainer Cookies
        {
            get => this.cookies;

            set
            {
                if (value == null)
                {
                    return;
                }

                this.cookies = value;
            }
        }

        /// <summary>
        /// Gets or sets the HTTP client.
        /// </summary>
        internal HttpClient HttpClient { get; set; }

        /// <summary>
        /// Login in cloud server.
        /// </summary>
        /// <exception cref="AuthorizeException">Authorization exception.</exception>
        /// <returns>True or false result of operation.</returns>
        public async Task<bool> Login()
        {
            await this.CheckAuthorization(true);
            HttpClientInitializer.Create(this, Urls.BaseMailRuAuth);
            var content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Login", this.Email),
                new KeyValuePair<string, string>("Domain", "mail.ru"),
                new KeyValuePair<string, string>("Password", SecureStringToString(this.password))
            });

            var responseMessage = await this.HttpClient.PostAsync(Urls.Auth, content);
            if (!responseMessage.IsSuccessStatusCode)
            {
                return false;
            }

            responseMessage = await this.HttpClient.GetAsync(Urls.EnsureSdc);
            if (!responseMessage.IsSuccessStatusCode)
            {
                return false;
            }

            HttpClientInitializer.Create(this, Urls.BaseMailRuCloud);
            var responseStr = await this.HttpClient.GetStringAsync(Urls.AuthToken);
            this.AuthToken = responseStr.Deserialize<AuthToken>().Token;
            var rates = await this.GetRates();
            this.ActivatedTariffs = rates.Where(x => x.IsActive).ToList();
            return true;
        }

        /// <summary>
        /// Check the client current authorization. 
        /// Do not call this method always before any request, by default it's enabled already.
        /// </summary>
        /// <returns>True - if client is in the system now.</returns>
        public async Task<bool> CheckAuthorization()
        {
            try
            {
                await this.CheckAuthorization(false);
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Get disk usage for account.
        /// </summary>
        /// <returns>Returns Total/Free/Used size.</returns>
        public async Task<DiskUsage> GetDiskUsage()
        {
            return await this.GetDiskUsageInternal(true);
        }

        /// <summary>
        /// Check authorization options.
        /// </summary>
        /// <param name="baseCheckout">If true will check only Login and Password.</param>
        /// <returns>The simple task.</returns>
        internal async Task CheckAuthorization(bool baseCheckout)
        {
            if (string.IsNullOrEmpty(this.Email))
            {
                throw new NotAuthorizedException("Is not defined.", nameof(this.Login));
            }

            if (this.password.Length == 0)
            {
                throw new NotAuthorizedException("Is not defined.", nameof(this.password));
            }

            if (!baseCheckout)
            {
                if (this.Cookies == null || this.Cookies.Count == 0)
                {
                    throw new NotAuthorizedException("Missing cookies.");
                }

                if (string.IsNullOrEmpty(this.AuthToken))
                {
                    throw new NotAuthorizedException("Missing authorization token.");
                }

                await this.GetDiskUsageInternal(false);
            }
        }

        /// <summary>
        /// Get disk usage for account.
        /// </summary>
        /// <param name="checkAuthorization">When true, will be checked the authorization to MAILRU.</param>
        /// <returns>Returns Total/Free/Used size.</returns>
        internal async Task<DiskUsage> GetDiskUsageInternal(bool checkAuthorization)
        {
            if (checkAuthorization)
            {
                await this.CheckAuthorization(false);
            }

            var responseMsg = await this.HttpClient.GetAsync(Urls.DiskSpace, this.Email, this.AuthToken);
            if (!responseMsg.IsSuccessStatusCode)
            {
                throw new NotAuthorizedException("The client is not authorized.");
            }

            var responseStr = await responseMsg.Content.ReadAsStringAsync();
            var responseParsed = responseStr.Deserialize<JToken>();
            return new DiskUsage
            {
                Total = new Size((long)responseParsed["bytes_total"] * 1024L * 1024L),
                Used = new Size((long)responseParsed["bytes_used"] * 1024L * 1024L)
            };
        }

        private static string SecureStringToString(SecureString value)
        {
            var valuePtr = IntPtr.Zero;
            try
            {
                valuePtr = Marshal.SecureStringToGlobalAllocUnicode(value);
                return Marshal.PtrToStringUni(valuePtr);
            }
            finally
            {
                Marshal.ZeroFreeGlobalAllocUnicode(valuePtr);
            }
        }

        /// <summary>
        /// Get the activated tariffs.
        /// </summary>
        /// <returns>The tariffs list.</returns>
        private async Task<List<Rate>> GetRates()
        {
            try
            {
                await this.CheckAuthorization(false);
                var responseStr = await this.HttpClient.GetStringAsync(Urls.Rates, this.Email, this.AuthToken);
                return responseStr.Deserialize<Rates>().Items.ToList();
            }
            catch
            {
                return new List<Rate>();
            }
        }
    }
}
