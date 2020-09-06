using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Device.Net.Reactive
{

    /// <summary>
    /// This class is a work in progress. It is not production ready.
    /// </summary>
    public class ReactiveDeviceManager : IReactiveDeviceManager, IDisposable
    {
        #region Fields
        private readonly ILogger<ReactiveDeviceManager> _logger;
        private readonly Func<IDevice, Task> _initializeDeviceAction;
        private IDevice _selectedDevice;
        private readonly Queue<IRequest> _queuedRequests = new Queue<IRequest>();
        private readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _semaphoreSlim2 = new SemaphoreSlim(1, 1);
        private readonly DeviceNotify _notifyDeviceInitialized;
        private readonly NotifyDeviceException _notifyDeviceException;
        private readonly DevicesNotify _notifyConnectedDevices;
        private bool isDisposed;
        private readonly int _pollMilliseconds;
        #endregion

        #region Protected Properties
        protected IDeviceManager DeviceManager { get; }
        #endregion

        #region Public Properties
        /// <summary>
        /// Placeholder. Don't use. This functionality will be injected it
        /// </summary>
        public bool FilterMiddleMessages { get; set; }
        public IList<FilterDeviceDefinition> FilterDeviceDefinitions { get; }


        public IObservable<IReadOnlyCollection<ConnectedDevice>> ConnectedDevicesObservable { get; }

        public IDevice SelectedDevice
        {
            get => _selectedDevice;
            private set
            {
                _selectedDevice = value;
                _notifyDeviceInitialized(value != null ? new ConnectedDevice { DeviceId = value.DeviceId } : null);
            }
        }
        #endregion

        #region Constructor
        /// <summary>
        /// 
        /// </summary>
        /// <param name="deviceManager">/param>
        /// <param name="selectedDeviceObservable">Listens for a selected device</param>
        /// <param name="initializedDeviceObserver">Tells others that the device was initialized</param>
        /// <param name="connectedDevicesObserver">Tells others which devices are connected</param>
        /// <param name="loggerFactory"></param>
        public ReactiveDeviceManager(
            IDeviceManager deviceManager,
            DeviceNotify notifyDeviceInitialized,
            DevicesNotify notifyConnectedDevices,
            NotifyDeviceException notifyDeviceException,
            ILoggerFactory loggerFactory,
            Func<IDevice, Task> initializeDeviceAction,
            IList<FilterDeviceDefinition> filterDeviceDefinitions,
            int pollMilliseconds
            )
        {

            _notifyDeviceInitialized = notifyDeviceInitialized ?? throw new ArgumentNullException(nameof(notifyDeviceInitialized));
            _notifyDeviceException = notifyDeviceException ?? throw new ArgumentNullException(nameof(notifyDeviceException));
            _notifyConnectedDevices = notifyConnectedDevices ?? throw new ArgumentNullException(nameof(notifyConnectedDevices));

            DeviceManager = deviceManager;
            _logger = loggerFactory.CreateLogger<ReactiveDeviceManager>();

            FilterDeviceDefinitions = filterDeviceDefinitions;

            _initializeDeviceAction = initializeDeviceAction;
            _pollMilliseconds = pollMilliseconds;
        }
        #endregion

        #region Public Methods
        public void Start()
        {
            Task.Run(async () =>
            {
                while (!isDisposed)
                {
                    var devices = await DeviceManager.GetDevicesAsync(FilterDeviceDefinitions);
                    var lists = devices.Select(d => new ConnectedDevice { DeviceId = d.DeviceId }).ToList();
                    _notifyConnectedDevices(lists);
                    await Task.Delay(TimeSpan.FromMilliseconds(_pollMilliseconds));
                }
            });
        }

        public void SelectDevice(DeviceSelectedArgs connectedDevice) => _ = InitializeDeviceAsync(connectedDevice.ConnectedDevice);

        public async Task<TResponse> WriteAndReadAsync<TResponse>(IRequest request, Func<byte[], TResponse> convertFunc)
        {
            if (SelectedDevice == null) throw new InvalidOperationException("No device selected and initialized");

            try
            {
                await _semaphoreSlim.WaitAsync();
                var writeBuffer = request.ToArray();
                var readBuffer = await SelectedDevice.WriteAndReadAsync(writeBuffer);
                return convertFunc != null ? convertFunc(readBuffer) : default;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);

                if (ex is IOException)
                {
                    _notifyDeviceException(new ConnectedDevice { DeviceId = SelectedDevice?.DeviceId }, ex);
                    //The exception was an IO exception so disconnect the device
                    //The listener should reconnect

                    SelectedDevice.Dispose();

                    SelectedDevice = null;
                }

                throw;
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        public void QueueRequest(IRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            _queuedRequests.Enqueue(request);
            ProcessQueue();
        }

        private async Task ProcessQueue()
        {
            try
            {
                await _semaphoreSlim2.WaitAsync();

                IRequest mostRecentRequest = null;

                if (_queuedRequests.Count == 0) return;

                //Eat requests except for the most recent one
                while (_queuedRequests.Count > 0)
                {
                    mostRecentRequest = _queuedRequests.Dequeue();
                }

                await WriteAndReadAsync<object>(mostRecentRequest, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
            finally
            {
                _semaphoreSlim2.Release();
            }
        }
        #endregion

        #region Private Methods
        private async Task InitializeDeviceAsync(ConnectedDevice connectedDevice)
        {
            try
            {
                if (connectedDevice == null)
                {
                    _logger.LogInformation("Initialize requested but device was null");
                    return;
                }

                var device = DeviceManager.GetDevice(new ConnectedDeviceDefinition(connectedDevice.DeviceId));
                await _initializeDeviceAction(device);

                _logger.LogInformation("Device initialized {deviceId}", connectedDevice.DeviceId);
                SelectedDevice = device;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                _notifyDeviceException(connectedDevice, ex);
                SelectedDevice = null;
            }
        }

        public void Dispose() => isDisposed = true;
        #endregion
    }
}