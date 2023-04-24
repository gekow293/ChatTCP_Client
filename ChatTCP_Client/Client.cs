using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatTCP_Client
{
    internal class Client
    {
        public static event EventHandler<string> eventMessage;
        public static event EventHandler<string> eventAddClient;
        public static event EventHandler<List<string>> eventAddClients;

        private readonly object syncFileObj = new object();
        private AutoResetEvent resetEventMessage;
        private AutoResetEvent resetEventFile;

        private string host;
        private int port;
        private TcpClient client;
        private string userName;
        private string destUserName;
        private string message;

        public Client(string name)
        {
            host = "127.0.0.1";
            port = 8888;
            client = new TcpClient();
            userName = name;
            message = "";

            resetEventMessage = new AutoResetEvent(false);
            resetEventFile = new AutoResetEvent(false);
            MainWindow.enterMessage += MainWindow_enterMessage;
            MainWindow.enterFile += MainWindow_enterFile;
            MainWindow.eventSetDestClient += MainWindow_eventSetDestClient;
        }

        // выбор адресата сообщений
        private void MainWindow_eventSetDestClient(object sender, string e)
        {
            destUserName = e;
        }

        // нажатие отправки файла
        private void MainWindow_enterFile(object sender, EventArgs e)
        {
            resetEventFile.Set();
        }

        // нажатие отправки сообщения
        private void MainWindow_enterMessage(object sender, string e)
        {
            message = e.ToString();
            resetEventMessage.Set();
        }

        public async Task StartClient()
        {
            StreamReader Reader = null;
            StreamWriter Writer = null;
            NetworkStream stream = null;

            try
            {
                client.Connect(host, port); //подключение клиента
                stream = client.GetStream();
                Reader = new StreamReader(stream, Encoding.Default);
                Writer = new StreamWriter(stream, Encoding.Default);
                if (Writer is null || Reader is null) return;

                // запускаем новый поток для получения данных
                _ = Task.Run(() => ReceiveMessageAsync(Reader, stream));

                // запускаем поток отправки файлов
                _ = Task.Run(() => SendFileAsync(stream));

                // запускаем ввод сообщений
                await SendMessageAsync(Writer);
            }
            catch (Exception ex)
            {
                // сообщение о сбое
                eventMessage?.Invoke(null, ex.Message);
            }
        }


        // отправка сообщений
        async Task SendMessageAsync(StreamWriter writer)
        {
            // сначала отправляем имя
            await writer.WriteLineAsync( userName);
            await writer.FlushAsync();

            while (true)
            {
                // ждем отправки
                resetEventMessage.WaitOne();

                if (message != "")
                {
                    await writer.WriteLineAsync(destUserName + "&&&" + message);
                    await writer.FlushAsync();
                }
            }
        }

        // получение сообщений и файлов
        async Task ReceiveMessageAsync(StreamReader reader, NetworkStream stream)
        {
            while (true)
            {
                try
                {
                    // считываем ответ в виде строки
                    string message = await reader.ReadLineAsync();

                    // новый контакт вошел в чат
                    if(message.Contains("вошел в чат"))
                        eventAddClient?.Invoke(null, message);
                    
                    // если пустой ответ, ничего не выводим на консоль
                    if (string.IsNullOrEmpty(message)) continue;
                    // если пришел файл
                    if (message.Contains("Content-length:") && message.Contains("Filename:"))  
                        await ReceiveFileAsync(stream, message); 
                    else
                    {   // прием контактов в список контактов
                        if (message.Contains("ListUsersAfterIncoming:"))
                        {
                            var listUsers = message.Split(new string[] { "/" }, StringSplitOptions.None).ToList().Where(x=>x != "ListUsersAfterIncoming:").ToList();
  
                            eventAddClients?.Invoke(null, listUsers);
                        }
                        // сообщение 
                        else eventMessage?.Invoke(null, message);
                    }
                }
                catch (Exception e)
                {
                    // сообщение о сбое
                    eventMessage?.Invoke(null, e.Message);
                    break;
                }
            }
        }


        // отправка файлов
        async Task SendFileAsync(NetworkStream stream)
        {        
            while (true)
            {
                // ждем отправки
                resetEventFile.WaitOne();

                await SendAsync(stream);  
            }
        }

        // функция отправки файла
        public async Task SendAsync(NetworkStream stream)
        {
            try
            {
                OpenFileDialog openFileDialog = new OpenFileDialog();

                openFileDialog.InitialDirectory = "c:\\";
                openFileDialog.Filter = "txt files (*.txt)|*.txt|All files (*.*)|*.*";
                openFileDialog.FilterIndex = 2;
                openFileDialog.RestoreDirectory = true;

                string SendFileName = null;

                if (openFileDialog.ShowDialog() == true)
                {
                    SendFileName = openFileDialog.FileName;

                    FileInfo fi = new FileInfo(SendFileName);

                    int bufferSize = 1024;
                    byte[] buffer = null;
                    byte[] header = null;

                    FileStream fs = null;
                    lock (syncFileObj)
                        fs = new FileStream(SendFileName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

                    int bufferCount = Convert.ToInt32(Math.Ceiling((double)fs.Length / (double)bufferSize));

                    string headerStr = "Content-length:" + fs.Length.ToString() + "$Filename:" + fi.Name + "$UserName:" + userName + "$DestUser:" + destUserName + "\r\n";
                    header = new byte[bufferSize];
                    Array.Copy(Encoding.Default.GetBytes(headerStr), header, Encoding.Default.GetBytes(headerStr).Length);

                    await stream.WriteAsync(header, 0, header.Length);
                    await stream.FlushAsync();

                    for (int i = 0; i < bufferCount; i++)
                    {
                        buffer = new byte[bufferSize];
                        int size = fs.Read(buffer, 0, bufferSize);

                        await stream.WriteAsync(buffer, 0, buffer.Length);
                    }

                    eventMessage?.Invoke(null, "Отправлен файл: " + fi.Name);
                    fs.Close();

                    await stream.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                // сообщение о сбое
                eventMessage?.Invoke(null, ex.Message);
            }
        }

        // получение файла
        public async Task ReceiveFileAsync(NetworkStream stream, string headMessage)
        {
            try
            {
                int bufferSize = 1024;
                byte[] buffer = null;
                string filename = "";
                string username = "";
                int filesize = 0;

                string[] splitted = headMessage.Split(new string[] { "$" }, StringSplitOptions.None);
                Dictionary<string, string> headers = new Dictionary<string, string>();

                // разбор заголовка
                foreach (string s in splitted)
                    if (s.Contains(":"))
                        if (s.Contains("Content-length:"))
                        {
                            var f = s.Substring(s.IndexOf("C"));
                            headers.Add(f.Substring(0, f.IndexOf(":")), f.Substring(f.IndexOf(":") + 1));
                        }
                        else  headers.Add(s.Substring(0, s.IndexOf(":")), s.Substring(s.IndexOf(":") + 1));

                filesize = Convert.ToInt32(headers["Content-length"]);
                filename = headers["Filename"];
                username = headers["UserName"];

                int bufferCount = Convert.ToInt32(Math.Ceiling((double)filesize / (double)bufferSize));

                var pathFile = Environment.CurrentDirectory + "\\files\\";

                FileStream fs = null;
                lock (syncFileObj) 
                    fs = new FileStream(pathFile + filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

                while (filesize > 0)
                {
                    buffer = new byte[bufferSize];
                    int size = await stream.ReadAsync(buffer, 0, buffer.Length);
                    fs.Write(buffer, 0, size);
                    filesize -= size;
                }

                eventMessage?.Invoke(null, username + ": Получен файл: " + filename + "\r\n     В папке: " + pathFile);

                fs.Close();
            }
            catch (Exception ex)
            {
                // сообщение о сбое
                eventMessage?.Invoke(null, ex.Message);
            }  
        }

        // Метод упрощенного создания заголовка с информацией о размере данных отправляемых по сети.
        private byte[] GetHeader(int length)
        {
            string header = length.ToString();
            if (header.Length < 9)
            {
                string zeros = null;
                for (int i = 0; i < (9 - header.Length); i++) zeros += "0";

                header = zeros + header;
            }

            byte[] byteheader = Encoding.Default.GetBytes(header);

            return byteheader;
        }
    }

    // Класс для отправки текстового сообщения и
    // информации о пересылаемых байтах следующих последними в потоке сетевых данных.
    [Serializable]
    class SendInfo
    {
        public string message;
        public string filename;
        public int filesize;
    }
}
