using SteamKit2.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamWebAPIAccess.Authentication
{
    public class S24kAuthenticator : IAuthenticator
    {
        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            throw new NotImplementedException();
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            throw new NotImplementedException();
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            throw new NotImplementedException();
        }
    }
}
