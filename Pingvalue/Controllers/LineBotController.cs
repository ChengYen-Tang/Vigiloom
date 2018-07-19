using System;
using System.Collections.Generic;
using System.Linq;
using Pingvalue;
using System.Net.Http;
using System.Web.Http;

namespace Pingvalue.Controllers
{
    public class LineBotController : ApiController
    {
        public HttpResponseMessage Post()
        {
            try
            {
                //取得 http Post RawData(should be JSON)
                string postData = Request.Content.ReadAsStringAsync().Result;
                //剖析JSON
                var ReceivedMessage = isRock.LineBot.Utility.Parsing(postData);
                //回覆訊息
                if (ReceivedMessage.events[0].source.groupId == null)
                    isRock.LineBot.Utility.ReplyMessage(ReceivedMessage.events[0].replyToken,AppConfig.LineRetornMessage ,AppConfig.LineToken);

                //回覆API OK
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
            catch
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            }
        }
    }
}
