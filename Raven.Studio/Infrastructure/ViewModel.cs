﻿using System;
using System.Net;
using System.Reactive;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Raven.Studio.Infrastructure
{
    public class ViewModel : Model
    {
        private Subject<Unit> unloadedSubject;

        public void NotifyViewLoaded()
        {
            OnViewLoaded();
        }

        public void NotifyViewUnloaded()
        {
            if (unloadedSubject != null)
            {
                unloadedSubject.OnNext(Unit.Default);
            }

            OnViewUnloaded();
        }

        protected virtual void OnViewUnloaded()
        {
            
        }

        protected virtual void OnViewLoaded()
        {
            
        }

        protected IObservable<Unit> Unloaded
        {
            get { return unloadedSubject ?? (unloadedSubject = new Subject<Unit>()); }
        }
    }
}
