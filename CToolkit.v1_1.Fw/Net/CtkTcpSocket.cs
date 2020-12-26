using CToolkit.v1_1.Protocol;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CToolkit.v1_1.Net
{
    public class CtkTcpSocket : ICtkProtocolConnect, IDisposable
    {
        public bool IsActively = false;
        public Uri LocalUri;
        public Uri RemoteUri;
        protected Socket m_connSocket;
        protected bool m_isOpenRequesting = false;
        protected Socket m_workSocket;
        bool m_isReceiveLoop = false;
        ManualResetEvent mreIsConnecting = new ManualResetEvent(true);
        ManualResetEvent mreIsReceiving = new ManualResetEvent(true);
        ~CtkTcpSocket() { this.Dispose(false); }

        public Socket ConnSocket { get { return m_connSocket; } }
        public bool IsReceiveLoop { get { return m_isReceiveLoop; } private set { lock (this) m_isReceiveLoop = value; } }
        public bool IsWaitReceive { get { return this.mreIsReceiving.WaitOne(10); } }
        public Socket WorkSocket { get { return m_workSocket; } set { lock (this) { m_workSocket = value; } } }



        public bool CheckConnectStatus()
        {
            var socket = this.m_connSocket;
            if (socket == null) return false;
            if (!socket.Connected) return false;
            return !(socket.Poll(1000, SelectMode.SelectRead) && (socket.Available == 0));
        }
        public int ConnectIfNo(bool isAct)
        {
            this.IsActively = isAct;
            if (this.IsOpenRequesting || this.IsRemoteConnected) return 0;
            //if (this.IsLocalReadyConnect) return; //同步連線是等到連線才離開method, 不需判斷 IsLocalReadyConnect

            try
            {
                if (!Monitor.TryEnter(this, 3000)) return -1; // throw new CtkException("Cannot enter lock");
                this.m_isOpenRequesting = true;


                //若連線不曾建立, 或聆聽/連線被關閉
                if (this.m_connSocket == null || !this.m_connSocket.Connected)
                {
                    CtkNetUtil.DisposeSocket(this.m_connSocket);//Dispose舊的
                    this.m_connSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);//建立新的
                }


                if (isAct)
                {
                    if (this.LocalUri != null && !this.ConnSocket.IsBound)
                        this.ConnSocket.Bind(CtkNetUtil.ToIPEndPoint(this.LocalUri));
                    if (this.RemoteUri == null)
                        throw new CtkException("remote field can not be null");
                    this.ConnSocket.Connect(CtkNetUtil.ToIPEndPoint(this.RemoteUri));
                    this.OnFirstConnect(new CtkProtocolEventArgs() { Message = "Connect Success" });
                    this.WorkSocket = this.ConnSocket;
                }
                else
                {
                    if (this.LocalUri == null)
                        throw new Exception("local field can not be null");
                    if (!this.ConnSocket.IsBound)
                        this.ConnSocket.Bind(CtkNetUtil.ToIPEndPoint(this.LocalUri));
                    this.ConnSocket.Listen(100);
                    this.WorkSocket = this.ConnSocket.Accept();
                    this.OnFirstConnect(new CtkProtocolEventArgs() { Message = "Connect Success" });
                }

                return 0;
            }
            catch (Exception ex)
            {
                //一旦聆聽/連線失敗, 直接關閉所有Socket, 重新來過
                this.Disconnect();
                this.OnFailConnect(new CtkProtocolEventArgs() { Message = "Connect Fail" });
                throw ex;//同步型作業, 直接拋出例外, 不用寫Log
            }
            finally
            {
                this.m_isOpenRequesting = false;
                if (Monitor.IsEntered(this)) Monitor.Exit(this);
            }
        }
        public int ReceiveLoop()
        {
            try
            {
                if (!Monitor.TryEnter(this, 1000)) return -1;//進不去先離開
                if (!this.mreIsReceiving.WaitOne(10)) return 0;//接收中先離開
                this.mreIsReceiving.Reset();//先卡住, 不讓後面的再次進行

                this.IsReceiveLoop = true;
                while (this.IsReceiveLoop && !this.disposed)
                {
                    var ea = new CtkProtocolEventArgs()
                    {
                        Sender = this,
                    };

                    ea.TrxMessage = new CtkProtocolBufferMessage(1518);
                    var trxBuffer = ea.TrxMessage.ToBuffer();


                    trxBuffer.Length = this.WorkSocket.Receive(trxBuffer.Buffer, 0, trxBuffer.Buffer.Length, SocketFlags.None);
                    if (trxBuffer.Length == 0) break;//未收到資料卻停止接收, 只有封包Header? 先停止, 確認後再決定怎麼處理
                    this.OnDataReceive(ea);
                }
            }
            catch (Exception ex)
            {
                this.IsReceiveLoop = false;
                this.OnErrorReceive(new CtkProtocolEventArgs() { Message = "Read Fail" });
                //當 this.ConnSocket == this.WorkSocket 時, 代表這是 client 端
                if (this.ConnSocket != this.WorkSocket)
                    CtkNetUtil.DisposeSocket(this.WorkSocket);//Repeat/Loop執行, 一旦結束就釋放Socket
                throw ex;//同步型作業, 直接拋出例外, 不用寫Log
            }
            finally
            {
                this.mreIsReceiving.Set();//同步型的, 結束就可以Set
                if (Monitor.IsEntered(this)) Monitor.Exit(this);
            }
            return 0;
        }
        public void ReceiveLoopCancel()
        {
            this.IsReceiveLoop = false;
        }

        public int ReceiveOnce()
        {
            try
            {
                if (!Monitor.TryEnter(this, 1000)) return -1;//進不去先離開
                if (!this.mreIsReceiving.WaitOne(10)) return 0;//接收中先離開
                this.mreIsReceiving.Reset();//先卡住, 不讓後面的再次進行



                var ea = new CtkProtocolEventArgs()
                {
                    Sender = this,
                };

                ea.TrxMessage = new CtkProtocolBufferMessage(1518);
                var trxBuffer = ea.TrxMessage.ToBuffer();


                trxBuffer.Length = this.WorkSocket.Receive(trxBuffer.Buffer, 0, trxBuffer.Buffer.Length, SocketFlags.None);
                if (trxBuffer.Length == 0) return -1;
                this.OnDataReceive(ea);
            }
            catch (Exception ex)
            {
                this.OnErrorReceive(new CtkProtocolEventArgs() { Message = "Read Fail" });
                //當 this.ConnSocket == this.WorkSocket 時, 代表這是 client 端
                if (this.ConnSocket != this.WorkSocket)
                    CtkNetUtil.DisposeSocket(this.WorkSocket);//執行出現例外, 先釋放Socket
                throw ex;//同步型作業, 直接拋出例外, 不用寫Log
            }
            finally
            {
                this.mreIsReceiving.Set();//同步型的, 結束就可以Set
                if (Monitor.IsEntered(this)) Monitor.Exit(this);
            }
            return 0;
        }
        #region ICtkProtocolConnect

        public event EventHandler<CtkProtocolEventArgs> EhDataReceive;
        public event EventHandler<CtkProtocolEventArgs> EhDisconnect;
        public event EventHandler<CtkProtocolEventArgs> EhErrorReceive;
        public event EventHandler<CtkProtocolEventArgs> EhFailConnect;
        public event EventHandler<CtkProtocolEventArgs> EhFirstConnect;

        public object ActiveWorkClient { get { return this.WorkSocket; } set { this.WorkSocket = value as Socket; } }
        public bool IsLocalReadyConnect { get { return this.m_connSocket != null && this.m_connSocket.IsBound; } }
        public bool IsOpenRequesting { get { return this.m_isOpenRequesting; } }
        public bool IsRemoteConnected { get { return this.WorkSocket != null && this.WorkSocket.Connected; } }


        public int ConnectIfNo() { return this.ConnectIfNo(this.IsActively); }
        public void Disconnect()
        {
            this.mreIsReceiving.Set();

            try { CtkNetUtil.DisposeSocket(this.m_workSocket); }
            catch (Exception ex) { CtkLog.WarnNs(this, ex); }
            try { CtkNetUtil.DisposeSocket(this.m_connSocket); }
            catch (Exception ex) { CtkLog.WarnNs(this, ex); }

            this.OnDisconnect(new CtkProtocolEventArgs() { Message = "Disconnect method is executed" });

        }
        public void WriteMsg(CtkProtocolTrxMessage msg)
        {
            try
            {
                Monitor.Enter(this);
                var buffer = msg.ToBuffer();
                this.WorkSocket.Send(buffer.Buffer, buffer.Offset, buffer.Length, SocketFlags.None);
            }
            catch (Exception ex)
            {
                this.Disconnect();
                CtkLog.WarnNs(this, ex);
            }
            finally { if (Monitor.IsEntered(this)) Monitor.Exit(this); }
        }

        #endregion


        #region Event

        protected void OnDataReceive(CtkProtocolEventArgs ea)
        {
            if (this.EhDataReceive == null) return;
            this.EhDataReceive(this, ea);
        }
        protected void OnDisconnect(CtkProtocolEventArgs ea)
        {
            if (this.EhDisconnect == null) return;
            this.EhDisconnect(this, ea);
        }
        protected void OnErrorReceive(CtkProtocolEventArgs ea)
        {
            if (this.EhErrorReceive == null) return;
            this.EhErrorReceive(this, ea);
        }
        protected void OnFailConnect(CtkProtocolEventArgs ea)
        {
            if (this.EhFailConnect == null) return;
            this.EhFailConnect(this, ea);
        }
        protected void OnFirstConnect(CtkProtocolEventArgs ea)
        {
            if (this.EhFirstConnect == null) return;
            this.EhFirstConnect(this, ea);
        }

        #endregion





        #region Dispose

        bool disposed = false;
        public void Dispose()
        {
            Dispose(false);
            GC.SuppressFinalize(this);
        }
        public void DisposeSelf()
        {
            this.Disconnect();
            CtkUtilFw.DisposeObjTry(this.mreIsConnecting);
            CtkUtilFw.DisposeObjTry(this.mreIsReceiving);
            CtkEventUtil.RemoveEventHandlersOfOwnerByFilter(this, (dlgt) => true);

        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposed) return;
            if (disposing)
            {
                // Free any managed objects here.
            }
            // Free any unmanaged objects here.
            //
            this.DisposeSelf();
            disposed = true;
        }
        #endregion











    }

}




