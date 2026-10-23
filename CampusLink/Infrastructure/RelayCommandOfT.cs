using System;
using System.Windows.Input;

namespace CampusLink.Infrastructure
{
    /// <summary>带参数的同步命令，参数类型不匹配时静默忽略。</summary>
    public class RelayCommand<T> : ICommand where T : class
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter)
        {
            var typed = parameter as T;
            if (typed == null) return false;
            return _canExecute == null || _canExecute(typed);
        }

        public void Execute(object parameter)
        {
            var typed = parameter as T;
            if (typed != null) _execute(typed);
        }

        public void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
