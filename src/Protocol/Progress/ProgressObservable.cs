﻿using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OmniSharp.Extensions.LanguageServer.Protocol.Progress
{
    internal class ProgressObservable<T> : IProgressObservable<T>, IObserver<JToken>
    {
        private readonly Func<JToken, T> _factory;
        private readonly CompositeDisposable _disposable;
        private readonly ReplaySubject<JToken> _dataSubject;

        public ProgressObservable(ProgressToken token, Func<JToken, T> factory, Action disposal)
        {
            _factory = factory;
            _dataSubject = new ReplaySubject<JToken>(1);
            _disposable = new CompositeDisposable { Disposable.Create(_dataSubject.OnCompleted), Disposable.Create(disposal) };

            ProgressToken = token;
            if (_dataSubject is IDisposable disposable)
            {
                _disposable.Add(disposable);
            }
        }

        public ProgressToken ProgressToken { get; }
        public Type ParamsType { get; } = typeof(T);
        public void Next(JToken value) => OnNext(value);

        void IObserver<JToken>.OnCompleted() => _dataSubject.OnCompleted();

        void IObserver<JToken>.OnError(Exception error) => _dataSubject.OnError(error);

        public void OnNext(JToken value) => _dataSubject.OnNext(value);

        public void Dispose() => _disposable.Dispose();

        public IDisposable Subscribe(IObserver<T> observer) => _disposable.IsDisposed ? Disposable.Empty : _dataSubject.Select(_factory).Subscribe(observer);
    }
}
