﻿using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace MissionPlanner.Utilities
{
    public class TerrainFollow
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        bool issending = false;

        MAVLink.MAVLinkMessage lastmessage;
        MAVLink.mavlink_terrain_request_t lastrequest;

        private static ManualResetEvent mre = new ManualResetEvent(false);

        private IMAVLinkInterface _interface;

        public TerrainFollow(IMAVLinkInterface inInterface)
        {
            _interface = inInterface;

            log.Info("OnPacketReceived to packets");
            _interface.OnPacketReceived += _interface_OnPacketReceived;
        }

        private void _interface_OnPacketReceived(object sender, MAVLink.MAVLinkMessage e)
        {
            if(e.msgid == (uint)MAVLink.MAVLINK_MSG_ID.TERRAIN_REQUEST 
                || e.msgid == (uint)MAVLink.MAVLINK_MSG_ID.TERRAIN_REPORT)
            {
                ReceivedPacket(e);
            }
        }

        ~TerrainFollow()
        {
            _interface.OnPacketReceived -= _interface_OnPacketReceived;
        }

        public void UnSub()
        {
            log?.Info("OnPacketReceived remove to packets");
            _interface.OnPacketReceived -= _interface_OnPacketReceived;
        }

        private bool ReceivedPacket(MAVLink.MAVLinkMessage rawpacket)
        {
            if (rawpacket.msgid == (byte) MAVLink.MAVLINK_MSG_ID.TERRAIN_REQUEST)
            {
                MAVLink.mavlink_terrain_request_t packet =
                    rawpacket.ToStructure<MAVLink.mavlink_terrain_request_t>();

                if (issending)
                    return false;

                lastmessage = rawpacket;
                lastrequest = packet;

                log.Info("received TERRAIN_REQUEST " + packet.lat/1e7 + " " + packet.lon/1e7 + " space " +
                         packet.grid_spacing + " " + Convert.ToString((long) packet.mask, 2));

                // reset state to block
                mre.Reset();

                System.Threading.ThreadPool.QueueUserWorkItem(QueueSendGrid);
                // wait for thread to start
                mre.WaitOne();
            }
            else if (rawpacket.msgid == (byte) MAVLink.MAVLINK_MSG_ID.TERRAIN_REPORT)
            {
                MAVLink.mavlink_terrain_report_t packet =
                    rawpacket.ToStructure<MAVLink.mavlink_terrain_report_t>();
                log.Info("received TERRAIN_REPORT " + packet.lat/1e7 + " " + packet.lon/1e7 + " " + packet.loaded + " " +
                         packet.pending);
            }
            return false;
        }

        private void QueueSendGrid(object nothing)
        {
            issending = true;
            // trigger start
            mre.Set();
            try
            {
                // 8 across - 7 down
                // cycle though the bitmask to check what we need to send (8*7)
                for (byte i = 0; i < 56; i++)
                {
                    // check to see if the ap requested this box.
                    if ((lastrequest.mask & ((ulong) 1 << i)) > 0)
                    {
                        // get the requested lat and lon
                        double lat = lastrequest.lat/1e7;
                        double lon = lastrequest.lon/1e7;

                        // get the distance between grids
                        int bitgridspacing = lastrequest.grid_spacing*4;

                        // get the new point, based on our current bit.
                        var newplla = new PointLatLngAlt(lat, lon).gps_offset(bitgridspacing*(i%8),
                            bitgridspacing*(int) Math.Floor(i/8.0));

                        // send a 4*4 grid, based on the lat lon of the bitmask
                        SendGrid(newplla.Lat, newplla.Lng, lastrequest.grid_spacing, i);

                        // 12hz = (43+6) * 12 = 588 bps
                        System.Threading.Thread.Sleep(1000/12);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
            finally
            {
                issending = false;
            }
        }

        void SendGrid(double lat, double lon, ushort grid_spacing, byte bit)
        {
            log.Info("SendGrid " + lat + " " + lon + " space " + grid_spacing + " bit " + bit);

            MAVLink.mavlink_terrain_data_t resp = new MAVLink.mavlink_terrain_data_t();
            resp.grid_spacing = grid_spacing;
            resp.lat = lastrequest.lat;
            resp.lon = lastrequest.lon;
            resp.gridbit = bit;
            resp.data = new short[16];

            for (int i = 0; i < (4*4); i++)
            {
                int x = i%4;
                int y = i/4;

                PointLatLngAlt plla = new PointLatLngAlt(lat, lon).gps_offset(x*grid_spacing, y*grid_spacing);

                var alt = srtm.getAltitude(plla.Lat, plla.Lng);

                // check where the alt returned came from.
                if (alt.currenttype == srtm.tiletype.invalid)
                    return;

                resp.data[i] = (short) alt.alt;
            }

            _interface.sendPacket(resp, lastmessage.sysid, lastmessage.compid);
        }

        public void checkTerrain(double lat, double lon)
        {
            MAVLink.mavlink_terrain_check_t packet = new MAVLink.mavlink_terrain_check_t();

            packet.lat = (int) (lat*1e7);
            packet.lon = (int) (lon*1e7);

            _interface.sendPacket(packet, _interface.sysidcurrent, _interface.compidcurrent);
        }
    }
}