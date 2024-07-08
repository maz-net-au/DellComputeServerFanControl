//using NvAPIWrapper.Native.GPU;
//using NvAPIWrapper.Native.Interfaces.GPU;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace ComputeServerTempMonitor
//{
//    public struct PState : IPerformanceStates20Info
//    {
//        IPerformanceStates20Info _available;
//        PerformanceStateId _target;

//        public PState(IPerformanceStates20Info available, PerformanceStateId target) 
//        { 
//            _available = available;
//            _target = target;
//        }
//        public IReadOnlyDictionary<PerformanceStateId, IPerformanceStates20ClockEntry[]> Clocks
//        {
//            get
//            {
//                PerformanceStateId pid = _target;
//                return _available.Clocks.Where(x => x.Key == pid).ToDictionary(x => x.Key, x => x.Value);
//            }
//        }
//        public IPerformanceStates20VoltageEntry[] GeneralVoltages => _available.GeneralVoltages;

//        public bool IsEditable => false;

//        public IPerformanceState20[] PerformanceStates
//        {
//            get
//            {
//                PerformanceStateId pid = _target;
//                return _available.PerformanceStates.Where(x => x.StateId == pid).ToArray();
//            }
//        }

//        public IReadOnlyDictionary<PerformanceStateId, IPerformanceStates20VoltageEntry[]> Voltages
//        {
//            get
//            {
//                PerformanceStateId pid = _target;
//                return _available.Voltages.Where(x => x.Key == pid).ToDictionary(x => x.Key, x => x.Value);
//            }
//        }
//    }
//}
