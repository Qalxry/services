using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Services.Core.Models
{
    public class Service : INotifyPropertyChanged
    {
        private string _status = "未知";
        private int _pid;
        private string _name = string.Empty;
        private string _exePath = string.Empty;
        private string? _args;
        private string? _workingDir;
        private string? _description;
        private bool _autoRestart;
        private ServiceStartupType _startupType = ServiceStartupType.Auto;

        public string Id { get; set; } = string.Empty;

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public string ExePath
        {
            get => _exePath;
            set => SetField(ref _exePath, value);
        }

        public string? Args
        {
            get => _args;
            set => SetField(ref _args, value);
        }

        public string? WorkingDir
        {
            get => _workingDir;
            set => SetField(ref _workingDir, value);
        }

        public string? Description
        {
            get => _description;
            set => SetField(ref _description, value);
        }

        public string Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        public int Pid
        {
            get => _pid;
            set
            {
                if (SetField(ref _pid, value))
                {
                    OnPropertyChanged(nameof(HasPid));
                }
            }
        }

        public bool HasPid => Pid > 0;

        public ServiceStartupType StartupType
        {
            get => _startupType;
            set
            {
                if (SetField(ref _startupType, value))
                {
                    OnPropertyChanged(nameof(AutoStart));
                }
            }
        }

        public bool AutoStart => StartupType == ServiceStartupType.Auto;

        public bool AutoRestart
        {
            get => _autoRestart;
            set => SetField(ref _autoRestart, value);
        }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// 从另一个实例复制所有可变字段（用于刷新列表时保留同一对象引用）
        /// </summary>
        public void CopyFrom(Service other)
        {
            Name = other.Name;
            ExePath = other.ExePath;
            Args = other.Args;
            WorkingDir = other.WorkingDir;
            Description = other.Description;
            Status = other.Status;
            Pid = other.Pid;
            StartupType = other.StartupType;
            AutoRestart = other.AutoRestart;
            CreatedAt = other.CreatedAt;
            UpdatedAt = other.UpdatedAt;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public class ServiceConfig
    {
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public string? Args { get; set; }
        public string? WorkingDir { get; set; }
        public string? Description { get; set; }
        public bool AutoRestart { get; set; }
        public ServiceStartupType StartupType { get; set; } = ServiceStartupType.Auto;
    }

    public enum ServiceStartupType
    {
        Auto = 2,
        Manual = 3,
        Disabled = 4
    }
}
