﻿using System.Collections;
using System.Collections.Generic;
using LiteNetLibManager;
using LiteNetLib.Utils;

namespace Insthync.MMOG
{
    public class ResponseLoginMessage : BaseAckMessage
    {
        public override void DeserializeData(NetDataReader reader)
        {
            throw new System.NotImplementedException();
        }

        public override void SerializeData(NetDataWriter writer)
        {
            throw new System.NotImplementedException();
        }
    }
}