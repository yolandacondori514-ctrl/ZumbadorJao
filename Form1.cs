using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Media;
using System.Threading;
using System.IO;
using System.Runtime.InteropServices;

namespace ZumbidoJao
{
    public partial class Form1 : Form
    {
        // Buscar abeja.ico en carpetas relacionadas (directorio de ejecución y ascendiendo)
        private string? FindAbejaIcon()
        {
            try
            {
                string candidate = Path.Combine(AppContext.BaseDirectory, "abeja.ico");
                return File.Exists(candidate) ? candidate : null;
            }
            catch (Exception ex) { RegistrarError("Iconos", "Buscar abeja.ico", ex); }
            return null;
        }
        private NotifyIcon trayIcon = null!;
        private TextBox txtIpDestino = null!;
        private ComboBox cboCaja = null!;
        private Button btnEnviar = null!;
        private SoundPlayer soundPlayer = null!;
        private const int PUERTO = 8080;
        private const int MAX_CLIENTES_SIMULTANEOS = 16;
        private const int TIMEOUT_CLIENTE_MS = 5000;
        private static readonly TimeSpan VENTANA_DEDUPLICACION = TimeSpan.FromSeconds(10);
        private readonly CancellationTokenSource servidorCts = new();
        private readonly SemaphoreSlim clientesActivos = new(MAX_CLIENTES_SIMULTANEOS, MAX_CLIENTES_SIMULTANEOS);
        private readonly ConcurrentDictionary<Task, byte> tareasClientes = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> alertasRecientes = new();
        private TcpListener? listener;
        private Task? tareaServidor;
        private bool cerrarAplicacion;
        private bool cierreEnCurso;
        private bool cierreCompletado;

        private static void RegistrarError(string categoria, string operacion, Exception exception)
        {
            AppLogger.Error(categoria, operacion, exception);
        }

        private static void RegistrarAdvertencia(string categoria, string operacion, Exception? exception = null)
        {
            AppLogger.Warning(categoria, operacion, exception);
        }

        // P/Invoke para vibración con XInput (gamepad). Se envía y se detiene tras un periodo.
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed; public ushort wRightMotorSpeed; }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        // P/Invoke para parpadeo de ventana como alerta visual
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO {
            public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout;
        }

        // Aplica un degradado ligero: magenta claro -> azul y texto blanco
        private void ApplyGradientStyle(Button btn)
        {
            if (btn == null) return;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.ForeColor = Color.White;

            // magenta pigmento (tono más profundo) para color principal
            Color magentaPigmento = Color.FromArgb(204, 0, 102); // tono magenta pigmento
            btn.BackColor = magentaPigmento; // fallback

            // Redibujar con degradado (magenta pigmento -> azul) dando mayor presencia al magenta
            btn.Paint += (s, e) =>
            {
                try
                {
                    using (LinearGradientBrush brush = new LinearGradientBrush(btn.ClientRectangle, magentaPigmento, Color.DodgerBlue, 45f))
                    {
                        // degradado suave: magenta ocupa mayor parte y transición progresiva hacia azul
                        ColorBlend cb = new ColorBlend();
                        cb.Colors = new Color[] { magentaPigmento, magentaPigmento, Color.DodgerBlue };
                        cb.Positions = new float[] { 0f, 0.7f, 1f }; // magenta hasta 70%, luego transición a azul
                        brush.InterpolationColors = cb;

                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        e.Graphics.FillRectangle(brush, btn.ClientRectangle);
                    }
                    // dibujar texto centrado
                    TextRenderer.DrawText(e.Graphics, btn.Text, btn.Font, btn.ClientRectangle, btn.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                catch (Exception ex) { RegistrarError("UI", "Pintar botón degradado", ex); }
            };

            // efectos hover: ligero oscurecimiento en hover para retroalimentación
            btn.MouseEnter += (s, e) => { btn.Invalidate(); };
            btn.MouseLeave += (s, e) => { btn.Invalidate(); };
            btn.Resize += (s, e) => { btn.Invalidate(); };
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        public Form1()
        {
            ConfigurarUI();
            ConfigurarTrayIcon();
            // Intentamos cargar un archivo WAV en la carpeta de la aplicación
            try
            {
                string rutaWav = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zumbido.wav");
                if (File.Exists(rutaWav))
                {
                    soundPlayer = new SoundPlayer(rutaWav);
                }
                else
                {
                    soundPlayer = new SoundPlayer();
                }
            }
            catch (Exception ex)
            {
                RegistrarError("Audio", "Inicializar zumbido.wav", ex);
                soundPlayer = new SoundPlayer();
            }
            tareaServidor = IniciarServidor();
        }

        // Añadir sombra al formulario principal (efecto de iluminación sutil)
        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        private void ConfigurarUI()
        {
            this.Text = "Sistema de Asistencia - Caja";
            this.Size = new Size(340, 240);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MaximizeBox = false;
            this.ShowInTaskbar = true;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.BackColor = Color.White;

            // Panel contenedor superior para organizar los inputs
            Panel panelCampos = new Panel
            {
                Location = new Point(20, 20),
                Size = new Size(280, 100)
            };

            // Etiquetas y campos con colores uniformes
            Color colorPrimario = Color.FromArgb(21, 67, 96); // azul corporativo

            Label lblIp = new Label { Text = "IP del JAO:", Location = new Point(0, 10), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = colorPrimario };
            txtIpDestino = new TextBox { Location = new Point(100, 7), Width = 170, Font = new Font("Segoe UI", 9), BackColor = Color.White, ForeColor = Color.Black, BorderStyle = BorderStyle.FixedSingle };

            Label lblCaja = new Label { Text = "Caja Solicitante N°:", Location = new Point(0, 55), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = colorPrimario };

            // Campo seleccionable del 1 al 10
            cboCaja = new ComboBox
            {
                Location = new Point(140, 52),
                Width = 130,
                Font = new Font("Segoe UI", 9),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.White,
                ForeColor = Color.Black
            };
            for (int i = 1; i <= 10; i++) cboCaja.Items.Add(i.ToString());
            cboCaja.SelectedIndex = 0;

            panelCampos.Controls.Add(lblIp);
            panelCampos.Controls.Add(txtIpDestino);
            panelCampos.Controls.Add(lblCaja);
            panelCampos.Controls.Add(cboCaja);

            // Botón de acción principal con estilo corporativo (Azul oscuro / Marino)
            btnEnviar = new Button
            {
                Text = "Solicitar Asistencia al JAO",
                Location = new Point(20, 130),
                Size = new Size(280, 42),
                BackColor = Color.FromArgb(21, 67, 96),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnEnviar.FlatAppearance.BorderSize = 0;
            btnEnviar.Click += BtnEnviar_Click;
            // Aplicar estilo degradado magenta->azul
            ApplyGradientStyle(btnEnviar);

            // Texto de pie de página discreto
            Label lblFooter = new Label
            {
                Text = "Gerencia de Operaciones",
                Location = new Point(20, 180),
                Size = new Size(280, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8, FontStyle.Italic)
            };

            this.Controls.Add(panelCampos);
            this.Controls.Add(btnEnviar);
            this.Controls.Add(lblFooter);

            this.FormClosing += Form1_FormClosing;
        }

        private void CerrarAplicacion()
        {
            cerrarAplicacion = true;
            Close();
        }

        private async void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!cerrarAplicacion)
            {
                e.Cancel = true;
                try { this.ShowInTaskbar = false; } catch { }
                Hide();
                return;
            }

            if (cierreCompletado)
                return;

            e.Cancel = true;
            if (cierreEnCurso)
                return;

            cierreEnCurso = true;
            await DetenerServidorAsync();
            cierreCompletado = true;
            Close();
        }

        private async Task DetenerServidorAsync()
        {
            servidorCts.Cancel();
            listener?.Stop();

            if (tareaServidor is not null)
            {
                try { await tareaServidor; }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            }

            Task[] tareasPendientes = tareasClientes.Keys.ToArray();
            if (tareasPendientes.Length > 0)
            {
                try { await Task.WhenAll(tareasPendientes); }
                catch (OperationCanceledException) { }
            }

            try { trayIcon.Visible = false; } catch { }
            try { trayIcon.Dispose(); } catch { }
            try { trayIcon.ContextMenuStrip?.Dispose(); } catch { }
            try { soundPlayer?.Dispose(); } catch { }
            clientesActivos.Dispose();
            servidorCts.Dispose();
        }

        private void ConfigurarTrayIcon()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Abrir Panel", null, (s, e) => {
                try { this.ShowInTaskbar = true; } catch { }
                this.Show();
            });
            menu.Items.Add("Salir", null, (s, e) => CerrarAplicacion());

            trayIcon = new NotifyIcon
            {
                ContextMenuStrip = menu,
                Visible = false, // habilitar después de asignar icono para evitar icon vacío
                Text = "Zumbido P2P"
            };

            // Intentamos cargar tu ícono personalizado desde la carpeta de salida de la app.
            try
            {
                string? iconPath = FindAbejaIcon();
                if (!string.IsNullOrEmpty(iconPath))
                {
                    try { trayIcon.Icon = new Icon(iconPath); }
                    catch (Exception ex) { RegistrarError("Iconos", "Cargar icono de bandeja", ex); trayIcon.Icon = SystemIcons.Application; }
                    try { this.Icon = new Icon(iconPath); }
                    catch (Exception ex) { RegistrarError("Iconos", "Cargar icono del formulario", ex); }
                }
                else
                {
                    trayIcon.Icon = SystemIcons.Application;
                }

                // Mostrar el icono en la bandeja ahora que está asignado
                trayIcon.Visible = true;
            }
            catch (Exception ex)
            {
                RegistrarError("Iconos", "Configurar icono de bandeja", ex);
                try { trayIcon.Icon = SystemIcons.Information; trayIcon.Visible = true; }
                catch (Exception fallbackEx) { RegistrarError("Iconos", "Aplicar icono de fallback", fallbackEx); }
            }

            trayIcon.DoubleClick += (s, e) => {
                try { this.ShowInTaskbar = true; } catch { }
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            };
        }

        private async Task IniciarServidor()
        {
            listener = new TcpListener(IPAddress.Any, PUERTO);
            listener.Start();

            try
            {
                while (!servidorCts.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(servidorCts.Token);
                    }
                    catch (OperationCanceledException) when (servidorCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (SocketException) when (servidorCts.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!clientesActivos.Wait(0))
                    {
                        client.Dispose();
                        continue;
                    }

                    Task tareaCliente = ProcesarClienteAsync(client);
                    tareasClientes.TryAdd(tareaCliente, 0);
                    _ = tareaCliente.ContinueWith(
                        tarea => tareasClientes.TryRemove(tarea, out _),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            catch (ObjectDisposedException) when (servidorCts.IsCancellationRequested)
            {
            }
            catch (SocketException) when (servidorCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                RegistrarError("Red", "Bucle principal del servidor", ex);
            }
            finally
            {
                listener?.Stop();
                listener = null;
            }
        }

        private async Task ProcesarClienteAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (CancellationTokenSource clienteCts = CancellationTokenSource.CreateLinkedTokenSource(servidorCts.Token))
                {
                    clienteCts.CancelAfter(TIMEOUT_CLIENTE_MS);

                    using NetworkStream stream = client.GetStream();

                    byte[] buffer = new byte[1024];
                    int bytesLeidos = await stream.ReadAsync(buffer.AsMemory(), clienteCts.Token);
                    if (bytesLeidos == 0)
                        return;

                    string mensajeRecibido = Encoding.UTF8.GetString(buffer, 0, bytesLeidos);
                    string origen = string.Empty;
                    if (client.Client.RemoteEndPoint is IPEndPoint ep)
                        origen = ep.Address.ToString();

                    if (!RegistrarAlerta(origen, mensajeRecibido))
                        return;

                    string identidadEmisor = "TCP sin TLS";

                    try
                    {
                        soundPlayer?.Play();
                    }
                    catch (Exception ex) { RegistrarError("Audio", "Reproducir zumbido recibido", ex); }

                    try
                    {
                        bool vibrated = TryVibrateAnyGamepad(600);
                        if (!vibrated)
                        {
                            FlashWindowNotification();
                            try { trayIcon?.ShowBalloonTip(3000, "Asistencia requerida", mensajeRecibido, ToolTipIcon.Info); }
                            catch (Exception ex) { RegistrarError("UI", "Mostrar notificación de bandeja", ex); }
                        }
                    }
                    catch (Exception ex) { RegistrarError("Alertas", "Activar vibración o alerta visual", ex); }

                    if (!IsDisposed && IsHandleCreated)
                    {
                        try
                        {
                            BeginInvoke(new Action(() => _ = ActivarZumbidoAsync(mensajeRecibido, origen, identidadEmisor)));
                        }

                        catch (InvalidOperationException ex) { RegistrarError("UI", "Programar alerta en el hilo de UI", ex); }
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                RegistrarAdvertencia("Red", "Cliente cancelado o agotó el timeout", ex);
            }
            catch (IOException ex)
            {
                RegistrarError("Red", "Leer cliente TCP", ex);
            }
            catch (SocketException ex)
            {
                RegistrarError("Red", "Procesar socket del cliente", ex);
            }
            catch (Exception ex)
            {
                RegistrarError("Red", "Error inesperado al procesar cliente TCP", ex);
            }
            finally
            {
                clientesActivos.Release();
            }
        }

        private bool RegistrarAlerta(string origen, string mensaje)
        {
            string clave = $"{origen}|{mensaje}";
            DateTimeOffset ahora = DateTimeOffset.UtcNow;

            if (alertasRecientes.TryGetValue(clave, out DateTimeOffset ultima) && ahora - ultima < VENTANA_DEDUPLICACION)
                return false;

            alertasRecientes[clave] = ahora;

            foreach (KeyValuePair<string, DateTimeOffset> alerta in alertasRecientes)
            {
                if (ahora - alerta.Value >= VENTANA_DEDUPLICACION)
                    alertasRecientes.TryRemove(alerta.Key, out _);
            }

            return true;
        }

        // Intenta vibrar cualquier gamepad (0..3). Devuelve true si logró iniciar vibración en alguno.
        private bool TryVibrateAnyGamepad(int durationMs)
        {
            try
            {
                XINPUT_VIBRATION vib = new XINPUT_VIBRATION { wLeftMotorSpeed = 65535, wRightMotorSpeed = 65535 };
                for (uint i = 0; i < 4; i++)
                {
                    try
                    {
                        uint res = XInputSetState(i, ref vib);
                        if (res == 0)
                        {
                            // Detener en background después de durationMs
                            uint index = i;
                            Task.Run(() => {
                                Thread.Sleep(durationMs);
                                try { XINPUT_VIBRATION stop = new XINPUT_VIBRATION { wLeftMotorSpeed = 0, wRightMotorSpeed = 0 }; XInputSetState(index, ref stop); }
                                catch (Exception ex) { RegistrarError("XInput", "Detener vibración del gamepad", ex); }
                            });
                            return true;
                        }
                    }
                    catch (Exception ex) { RegistrarError("XInput", "Consultar gamepad", ex); }
                }
            }
            catch (Exception ex)
            {
                RegistrarError("XInput", "Inicializar XInput", ex);
            }
            return false;
        }

        // Parpadea la ventana para llamar la atención (alerta visual)
        private void FlashWindowNotification()
        {
            try
            {
                FLASHWINFO fwi = new FLASHWINFO();
                fwi.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                fwi.hwnd = this.Handle;
                fwi.dwFlags = 3; // FLASHW_ALL
                fwi.uCount = 3;
                fwi.dwTimeout = 0;
                FlashWindowEx(ref fwi);
            }
            catch (Exception ex) { RegistrarError("UI", "Parpadear ventana de alerta", ex); }
        }

        private async void BtnEnviar_Click(object? sender, EventArgs e)
        {
            string ip = txtIpDestino.Text.Trim();
            string caja = "1";
            // Protección rápida: evitar NullReference si cboCaja es null
            caja = cboCaja?.SelectedItem?.ToString() ?? "1";
            string mensaje = $"Se requiere la presencia del JAO en la Caja {caja}";

            if (string.IsNullOrEmpty(ip)) return;

            btnEnviar.Enabled = false;
            bool enviado = false;
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    await client.ConnectAsync(ip, PUERTO);
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] data = Encoding.UTF8.GetBytes(mensaje);
                        await stream.WriteAsync(data, 0, data.Length);
                        enviado = true;
                    }
                }
            }
            catch (Exception ex)
            {
                RegistrarError("Red", $"Enviar solicitud a {ip}", ex);
                MessageBox.Show($"No se pudo conectar a {ip}.\n{ex.Message}", "Error");
            }
            finally
            {
                btnEnviar.Enabled = true;
            }

            // Si se envió correctamente, reproducir sonido local y mostrar popup (igual que receptor)
            if (enviado)
            {
                try { soundPlayer?.Play(); }
                catch (Exception ex) { RegistrarError("Audio", "Reproducir confirmación del emisor", ex); }
                try { MostrarMensajeEscritorio(mensaje, ip); }
                catch (Exception ex) { RegistrarError("UI", "Mostrar confirmación del emisor", ex); }
            }
        }

        private async Task ActivarZumbidoAsync(string mensaje, string origenIp, string identidadEmisor)
        {
            // 1. Mostrar la ventana y hacerla temblar
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();

            // Intentar reproducir un WAV llamado "messenger.wav" (sonido del Messenger antiguo) si existe
            try
            {
                string soundPath = Path.Combine(AppContext.BaseDirectory, "messenger.wav");
                if (File.Exists(soundPath))
                {
                    using (var player = new SoundPlayer(soundPath))
                    {
                        player.Play(); // reproducir en background
                    }
                }
                else
                {
                    // Fallback: sonido nativo de Windows
                    SystemSounds.Exclamation.Play();
                }
            }
            catch (Exception ex)
            {
                RegistrarError("Audio", "Reproducir alerta del receptor", ex);
                SystemSounds.Exclamation.Play();
            }

            Point originalLocation = this.Location;
            Random rnd = new Random();

            for (int i = 0; i < 20; i++)
            {
                this.Location = new Point(originalLocation.X + rnd.Next(-10, 10), originalLocation.Y + rnd.Next(-10, 10));
                await Task.Delay(20);
            }
            this.Location = originalLocation;

            // 2. Mostrar la notificación nativa en la parte inferior derecha
            // Formato: tiempo visible en ms, título, texto, ícono.
            try { trayIcon.ShowBalloonTip(5000, "Mensaje Importante", mensaje, ToolTipIcon.Info); }
            catch (Exception ex) { RegistrarError("UI", "Mostrar alerta del receptor", ex); }

            // 3. Mostrar un mensaje personalizado y colorido en el centro de pantalla (más grande y vistoso)
            try
            {
                MostrarMensajeEscritorio(mensaje, origenIp, bloquearPantalla: true, identidadEmisor: identidadEmisor);
            }
            catch (Exception ex)
            {
                // No bloquear si hay algún fallo al crear la ventana personalizada
                RegistrarError("UI", "Crear alerta modal del receptor", ex);
            }
        }

        private void MostrarMensajeEscritorio(string mensaje, string origenIp, bool bloquearPantalla = false, string? identidadEmisor = null)
        {
            // Nuevo diseño: título con icono, texto instructivo, caja grande centrada,
            // botón ENTENDIDO y X de cierre, además de footer y sombra.
            int popupWidth = 520;
            int popupHeight = 220;

            Form popup = new Form();
            popup.FormBorderStyle = FormBorderStyle.None;
            popup.StartPosition = FormStartPosition.Manual;
            popup.Size = new Size(popupWidth, popupHeight);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            popup.Location = new Point(wa.Left + (wa.Width - popup.Width) / 2, wa.Top + (wa.Height - popup.Height) / 2);
            popup.TopMost = true;
            popup.ShowInTaskbar = true;
            // intentar usar el icono principal del formulario para que aparezca en la barra y en el popup
            try { if (this.Icon != null) popup.Icon = this.Icon; }
            catch (Exception ex) { RegistrarError("Iconos", "Asignar icono al popup", ex); }
            popup.Opacity = 0.99;

            // sombra detrás del popup
            Form shadow = new Form();
            shadow.FormBorderStyle = FormBorderStyle.None;
            shadow.StartPosition = FormStartPosition.Manual;
            shadow.Size = popup.Size;
            shadow.Location = new Point(popup.Left + 6, popup.Top + 6);
            shadow.BackColor = Color.Black;
            shadow.Opacity = 0.16;
            shadow.ShowInTaskbar = false;
            shadow.TopMost = true;

            // panel interior
            Panel inner = new Panel();
            inner.Location = new Point(4, 4);
            inner.Size = new Size(popup.Width - 8, popup.Height - 8);
            inner.BackColor = Color.White;

            // icono abeja en header (usar Icon del formulario si está disponible, si no intentar cargar abeja.ico)
            PictureBox picBee = new PictureBox();
            picBee.Size = new Size(36, 36);
            picBee.Location = new Point(12, 12);
            picBee.SizeMode = PictureBoxSizeMode.Zoom;
            picBee.BackColor = Color.Transparent;
            try
            {
                if (this.Icon != null)
                {
                    picBee.Image = this.Icon.ToBitmap();
                }
                else
                {
                    string? found = FindAbejaIcon();
                    if (!string.IsNullOrEmpty(found))
                    {
                        try { using (Icon ico = new Icon(found)) picBee.Image = ico.ToBitmap(); }
                        catch (Exception ex) { RegistrarError("Iconos", "Convertir icono de abeja a imagen", ex); }
                    }
                }
            }
            catch (Exception ex) { RegistrarError("Iconos", "Preparar icono de abeja", ex); }

            // pequeño icono de alerta (triángulo) entre la abeja y el título
            PictureBox alertIconSmall = new PictureBox();
            alertIconSmall.Size = new Size(20, 20);
            alertIconSmall.Location = new Point(56, 18);
            alertIconSmall.SizeMode = PictureBoxSizeMode.Zoom;
            alertIconSmall.BackColor = Color.Transparent;
            try { alertIconSmall.Image = SystemIcons.Warning.ToBitmap(); }
            catch (Exception ex) { RegistrarError("Iconos", "Crear icono de advertencia", ex); }

            // título con icono
            Label title = new Label();
            title.Text = "Atención requerida";
            title.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(30, 30, 30);
            title.AutoSize = true;
            title.Location = new Point(86, 14);

            // Nota: botón de cierre X eliminado (solo existe el botón ENTENDIDO)

            // texto instructivo centrado
            Label instruction = new Label();
            instruction.Text = $"Se requiere la presencia del JAO en:";
            instruction.Font = new Font("Segoe UI", 10F, FontStyle.Regular);
            instruction.ForeColor = Color.FromArgb(70, 70, 70);
            instruction.AutoSize = false;
            instruction.Size = new Size(inner.Width - 40, 22);
            instruction.Location = new Point(20, 60);
            instruction.TextAlign = ContentAlignment.MiddleCenter;

            // Extraer y mostrar la caja grande centrada (intentar leer número de caja del mensaje)
            string cajaText = "CAJA";
            try
            {
                int idx = mensaje.IndexOf("Caja", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string rest = mensaje.Substring(idx + 4).Trim();
                    // intentar limpiar caracteres no numéricos al inicio
                    rest = rest.TrimStart(new char[] { ':', ' ', '#' });
                    if (rest.Length > 0)
                        cajaText = "CAJA " + rest.Split(' ')[0];
                }
            }
            catch { }

            Label cajaLabel = new Label();
            cajaLabel.Text = cajaText;
            cajaLabel.Font = new Font("Segoe UI", 28F, FontStyle.Bold);
            cajaLabel.ForeColor = Color.FromArgb(28, 120, 200);
            cajaLabel.AutoSize = false;
            cajaLabel.Size = new Size(inner.Width - 80, 56);
            cajaLabel.Location = new Point(40, 86);
            cajaLabel.TextAlign = ContentAlignment.MiddleCenter;

            // botón ENTENDIDO centrado en el pie
            Button btnEntendido = new Button();
            btnEntendido.Text = "ENTENDIDO";
            btnEntendido.Size = new Size(160, 36);
            btnEntendido.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnEntendido.BackColor = Color.FromArgb(28, 120, 200);
            btnEntendido.ForeColor = Color.White;
            btnEntendido.FlatStyle = FlatStyle.Flat;
            btnEntendido.FlatAppearance.BorderSize = 0;
            btnEntendido.Location = new Point((inner.Width - btnEntendido.Width) / 2, inner.Height - btnEntendido.Height - 34);
            btnEntendido.Anchor = AnchorStyles.Bottom;
            btnEntendido.Cursor = Cursors.Hand;
            btnEntendido.Click += (s, e) =>
            {
                try
                {
                    // Cerrar popup y sombra de forma segura
                    try { if (popup != null && !popup.IsDisposed) { if (popup.InvokeRequired) popup.Invoke(new Action(() => popup.Close())); else popup.Close(); } } catch { }
                    try { if (shadow != null && !shadow.IsDisposed) { if (shadow.InvokeRequired) shadow.Invoke(new Action(() => shadow.Close())); else shadow.Close(); } } catch { }
                }
                catch { }
            };

            // Aplicar estilo degradado magenta->azul al botón ENTENDIDO
            ApplyGradientStyle(btnEntendido);

            // footer pequeño
            Label footer = new Label();
            footer.Text = "Gerencia de Operaciones";
            footer.Font = new Font("Segoe UI", 8F, FontStyle.Regular);
            footer.ForeColor = Color.FromArgb(120, 120, 120);
            footer.AutoSize = false;
            footer.Size = new Size(inner.Width, 20);
            footer.Location = new Point(0, inner.Height - 20);
            footer.TextAlign = ContentAlignment.MiddleCenter;

            // ensamblar controles
            inner.Controls.Add(picBee);
            inner.Controls.Add(alertIconSmall);
            inner.Controls.Add(title);
            inner.Controls.Add(instruction);
            inner.Controls.Add(cajaLabel);
            inner.Controls.Add(btnEntendido);
            inner.Controls.Add(footer);

            popup.Controls.Add(inner);

            // reproducir zumbador (si se cargó zumbido.wav) también en el popup del receptor
            try { soundPlayer?.Play(); } catch { }

            // preparar sombra para que no reciba clicks y mostrar sombra y popup
            try { shadow.Enabled = false; } catch { }
            popup.FormClosed += (s, e) => { try { if (!shadow.IsDisposed) shadow.Close(); } catch { } };
            shadow.Show();

            // animación de zumbido (temblor) del popup para llamar la atención
            Task.Run(() =>
            {
                try
                {
                    Point orig = popup.Location;
                    Random rnd = new Random();
                    for (int i = 0; i < 20; i++)
                    {
                        if (popup.IsDisposed || !popup.IsHandleCreated) break;
                        popup.Invoke(new Action(() => popup.Location = new Point(orig.X + rnd.Next(-10, 11), orig.Y + rnd.Next(-10, 11))));
                        Thread.Sleep(20);
                    }
                    if (!popup.IsDisposed && popup.IsHandleCreated)
                        popup.Invoke(new Action(() => popup.Location = orig));
                }
                catch { }
            });

            if (bloquearPantalla)
            {
                popup.ShowDialog(this);
            }
            else
            {
                popup.Show();
                popup.BringToFront();
                popup.Activate();
                try { popup.Focus(); } catch { }
                try { btnEntendido.Focus(); } catch { }
            }

            // animación de zumbido (temblor) del popup para llamar la atención
            Task.Run(() =>
            {
                try
                {
                    Point orig = popup.Location;
                    Random rnd = new Random();
                    for (int i = 0; i < 20; i++)
                    {
                        if (popup.IsDisposed || !popup.IsHandleCreated) break;
                        popup.Invoke(new Action(() => popup.Location = new Point(orig.X + rnd.Next(-10, 11), orig.Y + rnd.Next(-10, 11))));
                        Thread.Sleep(20);
                    }
                    if (!popup.IsDisposed && popup.IsHandleCreated)
                        popup.Invoke(new Action(() => popup.Location = orig));
                }
                catch { }
            });

            // Cerrar la sombra si el popup se cierra
            popup.FormClosed += (s, e) => { try { if (!shadow.IsDisposed) shadow.Close(); } catch { } };
        }

        protected override void SetVisibleCore(bool value)
        {
            // Oculta el formulario al arrancar la app
            if (!this.IsHandleCreated)
            {
                this.CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        // Nota: MostrarPopupEmisor eliminado. Se usa MostrarMensajeEscritorio para emisor y receptor.
    }
}